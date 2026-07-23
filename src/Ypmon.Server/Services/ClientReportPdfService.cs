using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using Ypmon.Server.Data;
using Ypmon.Shared;

namespace Ypmon.Server.Services;

/// <summary>Данные одного сервера для отчёта клиенту (готовые к вёрстке).</summary>
public sealed class ServerReportData
{
    public MonitoredServer Server { get; init; } = null!;
    public bool Offline { get; init; }
    public List<FolderStatusDto> Folders { get; init; } = new();
    public List<DiskStatusDto> Disks { get; init; } = new();
    public List<PhysicalDiskDto> PhysicalDisks { get; init; } = new();
    public List<RebootEvent> Reboots { get; init; } = new();
}

/// <summary>
/// Отчёт по обслуживанию для клиента: один PDF на клиента со всеми его серверами.
/// Данные берём из последнего отчёта каждого агента и накопленной истории перезагрузок.
/// </summary>
public class ClientReportPdfService
{
    private readonly AppDbContext _db;

    public ClientReportPdfService(AppDbContext db) => _db = db;

    static ClientReportPdfService()
    {
        // Community-лицензия QuestPDF: бесплатна для компаний с выручкой до $1 млн в год.
        QuestPDF.Settings.License = LicenseType.Community;
    }

    /// <summary>Шрифт с кириллицей. В Linux-образе ставится пакет fonts-dejavu-core, на Windows берём системный.</summary>
    private static string FontFamily => OperatingSystem.IsWindows() ? "Segoe UI" : "DejaVu Sans";

    public async Task<(byte[] pdf, string fileName)?> BuildAsync(int clientId, DateTimeOffset from, DateTimeOffset to)
    {
        var client = await _db.Clients.Include(c => c.Servers).FirstOrDefaultAsync(c => c.Id == clientId);
        if (client is null) return null;

        var settings = await _db.Settings.FirstOrDefaultAsync();
        var offlineThreshold = settings?.OfflineThresholdSeconds ?? 300;
        var now = DateTimeOffset.UtcNow;

        var servers = client.Servers.OrderBy(s => s.Name).ToList();
        var ids = servers.Select(s => s.Id).ToList();

        // Перезагрузки: по периоду отбираем в памяти — sqlite не умеет сравнивать DateTimeOffset в SQL.
        // Записей мало (единицы на сервер за месяц), поэтому берём свежий срез и режем его здесь.
        var reboots = (await _db.Reboots
                .Where(r => ids.Contains(r.ServerId))
                .OrderByDescending(r => r.Id)
                .Take(2000)
                .ToListAsync())
            .Where(r => r.At >= from && r.At <= to)
            .OrderBy(r => r.At)
            .ToList();

        var data = servers.Select(s => new ServerReportData
        {
            Server = s,
            Offline = s.IsOffline(offlineThreshold, now),
            Folders = ParseFolders(s.LastReportJson),
            Disks = Parse<List<DiskStatusDto>>(s.LastDisksJson) ?? new(),
            PhysicalDisks = Parse<List<PhysicalDiskDto>>(s.LastPhysicalDisksJson) ?? new(),
            Reboots = reboots.Where(r => r.ServerId == s.Id).ToList()
        }).ToList();

        var pdf = Render(client, data, from, to);
        var fileName = $"ypmon-{Slug(client.Name)}-{UiHelpers.Omsk(to, "yyyy-MM-dd")}.pdf";
        return (pdf, fileName);
    }

    private byte[] Render(Client client, List<ServerReportData> data, DateTimeOffset from, DateTimeOffset to)
    {
        var problems = data.Count(d => d.Offline || d.Server.LastOutcome >= JobOutcome.Warning || d.Server.BackupShrinkActive);
        var unexpected = data.Sum(d => d.Reboots.Count(r => r.Kind == "Unexpected"));

        return Document.Create(doc =>
        {
            doc.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(1.5f, Unit.Centimetre);
                page.DefaultTextStyle(t => t.FontFamily(FontFamily).FontSize(9).FontColor("#1f2937"));

                page.Header().Element(h => Header(h, client, from, to));
                page.Content().PaddingVertical(10).Column(col =>
                {
                    col.Spacing(14);
                    col.Item().Element(e => Summary(e, data, problems, unexpected));
                    foreach (var d in data)
                        col.Item().Element(e => ServerBlock(e, d, from, to));

                    if (data.Count == 0)
                        col.Item().Text("У клиента нет серверов под наблюдением.").FontColor("#6b7280");
                });

                page.Footer().Row(row =>
                {
                    row.RelativeItem().Text($"YPMonitor · сформирован {UiHelpers.Omsk(DateTimeOffset.UtcNow, "dd.MM.yyyy HH:mm")} (Омск)")
                        .FontSize(8).FontColor("#9ca3af");
                    row.ConstantItem(70).AlignRight().Text(t =>
                    {
                        t.DefaultTextStyle(s => s.FontSize(8).FontColor("#9ca3af"));
                        t.CurrentPageNumber();
                        t.Span(" из ");
                        t.TotalPages();
                    });
                });
            });
        }).GeneratePdf();
    }

    private static void Header(IContainer e, Client client, DateTimeOffset from, DateTimeOffset to)
    {
        e.BorderBottom(1).BorderColor("#e5e7eb").PaddingBottom(8).Row(row =>
        {
            row.RelativeItem().Column(c =>
            {
                c.Item().Text("Отчёт по обслуживанию серверов").FontSize(16).SemiBold();
                c.Item().PaddingTop(2).Text(client.Name).FontSize(11).FontColor("#374151");
            });
            row.ConstantItem(150).AlignRight().Column(c =>
            {
                c.Item().Text("Период").FontSize(8).FontColor("#9ca3af");
                c.Item().Text($"{UiHelpers.Omsk(from, "dd.MM.yyyy")} — {UiHelpers.Omsk(to, "dd.MM.yyyy")}").FontSize(10);
            });
        });
    }

    private static void Summary(IContainer e, List<ServerReportData> data, int problems, int unexpected)
    {
        e.Background("#f9fafb").Border(1).BorderColor("#e5e7eb").Padding(10).Column(col =>
        {
            col.Item().Text("Кратко").FontSize(12).SemiBold();
            col.Item().PaddingTop(6).Row(row =>
            {
                Tile(row, "Серверов", data.Count.ToString());
                Tile(row, "Без замечаний", (data.Count - problems).ToString());
                Tile(row, "С замечаниями", problems.ToString());
                Tile(row, "Аварийных выключений", unexpected.ToString());
            });
        });

        static void Tile(RowDescriptor row, string label, string value)
        {
            row.RelativeItem().Column(c =>
            {
                c.Item().Text(value).FontSize(18).SemiBold();
                c.Item().Text(label).FontSize(8).FontColor("#6b7280");
            });
        }
    }

    private static void ServerBlock(IContainer e, ServerReportData d, DateTimeOffset from, DateTimeOffset to)
    {
        var s = d.Server;
        var (mark, status) = d.Offline
            ? ("■", "нет связи с агентом")
            : s.LastOutcome switch
            {
                JobOutcome.Ok when !s.BackupShrinkActive => ("●", "в порядке"),
                JobOutcome.Ok => ("▲", "объём бэкапа резко уменьшился"),
                JobOutcome.Warning => ("▲", "есть предупреждения"),
                JobOutcome.Error => ("✕", "есть ошибки"),
                _ => ("○", "данных пока нет")
            };

        e.Column(col =>
        {
            col.Item().PaddingBottom(4).Row(row =>
            {
                row.RelativeItem().Text(t =>
                {
                    t.Span($"{mark} ").FontSize(11);
                    t.Span(s.Name).FontSize(12).SemiBold();
                    if (!string.IsNullOrWhiteSpace(s.LastMachineName))
                        t.Span($"  ({s.LastMachineName})").FontSize(9).FontColor("#6b7280");
                });
                row.ConstantItem(170).AlignRight().Text(status).FontSize(9).FontColor("#374151");
            });

            // Строка фактов: адрес, аптайм, связь, версия агента.
            col.Item().PaddingBottom(6).Text(t =>
            {
                t.DefaultTextStyle(x => x.FontSize(8).FontColor("#6b7280"));
                var parts = new List<string>();
                if (!string.IsNullOrWhiteSpace(s.IpAddress)) parts.Add($"IP {s.IpAddress}");
                parts.Add($"последняя связь: {UiHelpers.Ago(s.LastSeenAt)}");
                if (s.LastBootAt is { } boot) parts.Add($"без перезагрузки: {Uptime(boot)}");
                if (!string.IsNullOrWhiteSpace(s.LastAgentVersion)) parts.Add($"агент {s.LastAgentVersion}");
                t.Span(string.Join("   ·   ", parts));
            });

            if (d.Folders.Count > 0) col.Item().Element(x => FoldersTable(x, d.Folders));
            if (d.Disks.Count > 0) col.Item().PaddingTop(6).Element(x => DisksTable(x, d.Disks));
            if (d.PhysicalDisks.Count > 0) col.Item().PaddingTop(6).Element(x => DrivesTable(x, d.PhysicalDisks));
            col.Item().PaddingTop(6).Element(x => RebootsBlock(x, d.Reboots));
        });
    }

    private static void FoldersTable(IContainer e, List<FolderStatusDto> folders)
    {
        e.Column(col =>
        {
            col.Item().Text("Архивация").FontSize(9).SemiBold();
            col.Item().PaddingTop(3).Table(t =>
            {
                t.ColumnsDefinition(c => { c.RelativeColumn(3); c.RelativeColumn(2); c.RelativeColumn(2); c.RelativeColumn(3); });
                Head(t, "Папка", "Последний архив", "Объём", "Состояние");

                foreach (var f in folders)
                {
                    Cell(t, f.Name);
                    Cell(t, f.LastBackupAt is { } at ? UiHelpers.Omsk(at, "dd.MM.yyyy HH:mm") : "—");
                    Cell(t, f.LastFileSizeBytes > 0 ? UiHelpers.Bytes(f.LastFileSizeBytes) : "—");
                    Cell(t, f.Outcome switch
                    {
                        JobOutcome.Ok => "в порядке",
                        JobOutcome.Warning => Trim(f.Message) ?? "предупреждение",
                        JobOutcome.Error => Trim(f.Message) ?? "ошибка",
                        _ => "нет данных"
                    });
                }
            });
        });
    }

    private static void DisksTable(IContainer e, List<DiskStatusDto> disks)
    {
        e.Column(col =>
        {
            col.Item().Text("Место на дисках").FontSize(9).SemiBold();
            col.Item().PaddingTop(3).Table(t =>
            {
                t.ColumnsDefinition(c => { c.RelativeColumn(3); c.RelativeColumn(2); c.RelativeColumn(2); c.RelativeColumn(3); });
                Head(t, "Диск", "Объём", "Свободно", "Заполнен");

                foreach (var d in disks)
                {
                    Cell(t, string.IsNullOrWhiteSpace(d.Label) ? d.Name : $"{d.Name} ({d.Label})");
                    Cell(t, UiHelpers.Bytes(d.TotalBytes));
                    Cell(t, UiHelpers.Bytes(d.FreeBytes));
                    Cell(t, d.TotalBytes > 0 ? $"{100 - d.FreePercent:0.#}%" : "—");
                }
            });
        });
    }

    private static void DrivesTable(IContainer e, List<PhysicalDiskDto> drives)
    {
        e.Column(col =>
        {
            col.Item().Text("Накопители").FontSize(9).SemiBold();
            col.Item().PaddingTop(3).Table(t =>
            {
                t.ColumnsDefinition(c => { c.RelativeColumn(4); c.RelativeColumn(2); c.RelativeColumn(2); c.RelativeColumn(2); c.RelativeColumn(2); });
                Head(t, "Модель", "Состояние", "Наработка", "Износ", "Температура");

                foreach (var d in drives)
                {
                    Cell(t, d.Name);
                    Cell(t, d.Health switch
                    {
                        "Healthy" => "исправен",
                        "Warning" => "предупреждение",
                        "Unhealthy" => "неисправен",
                        _ => "нет данных"
                    });
                    // Наработку показываем годами: «52 000 часов» клиенту ни о чём не говорит.
                    Cell(t, d.PowerOnHours is { } h ? PowerOnText(h) : "—");
                    Cell(t, d.WearPercent is { } w ? $"{w}%"
                          : d.ReallocatedSectors is { } r ? (r > 0 ? $"{r} сект." : "нет") : "—");
                    Cell(t, d.TemperatureC is { } tc ? $"{tc} °C" : "—");
                }
            });

            var bad = drives.Where(d => d.ReallocatedSectors > 0).ToList();
            if (bad.Count > 0)
                col.Item().PaddingTop(3).Text($"Переназначенные сектора: {string.Join("; ", bad.Select(d => $"{d.Name} — {d.ReallocatedSectors}"))}. Рекомендуем запланировать замену.")
                    .FontSize(8).FontColor("#b45309");
        });
    }

    private static void RebootsBlock(IContainer e, List<RebootEvent> reboots)
    {
        // Считаем только включения: пара «выключение + включение» — это одна перезагрузка.
        var starts = reboots.Where(r => r.Kind == "Startup").ToList();
        var unexpected = reboots.Where(r => r.Kind == "Unexpected").ToList();

        e.Column(col =>
        {
            col.Item().Text("Перезагрузки").FontSize(9).SemiBold();
            if (reboots.Count == 0)
            {
                col.Item().PaddingTop(2).Text("За период не зафиксировано (или агент ещё не присылал историю).")
                    .FontSize(8).FontColor("#6b7280");
                return;
            }

            col.Item().PaddingTop(2).Text(t =>
            {
                t.DefaultTextStyle(x => x.FontSize(8));
                t.Span($"Включений: {starts.Count}");
                if (unexpected.Count > 0)
                    t.Span($"   ·   аварийных выключений: {unexpected.Count}").FontColor("#b91c1c");
            });

            foreach (var r in reboots.OrderByDescending(x => x.At).Take(12))
            {
                col.Item().Text(t =>
                {
                    t.DefaultTextStyle(x => x.FontSize(8).FontColor("#4b5563"));
                    t.Span($"{UiHelpers.Omsk(r.At, "dd.MM.yyyy HH:mm")} — {KindText(r.Kind)}");
                    if (!string.IsNullOrWhiteSpace(r.Reason))
                        t.Span($": {Trim(r.Reason, 140)}");
                });
            }
        });
    }

    private static string KindText(string kind) => kind switch
    {
        "Startup" => "включение",
        "Shutdown" => "штатное выключение",
        "Unexpected" => "аварийное выключение",
        "Initiated" => "перезагрузка по команде",
        _ => kind
    };

    // --- Вспомогательное ---

    private static void Head(TableDescriptor t, params string[] titles)
    {
        t.Header(h =>
        {
            foreach (var title in titles)
                h.Cell().BorderBottom(1).BorderColor("#e5e7eb").PaddingVertical(3)
                    .Text(title).FontSize(8).FontColor("#6b7280");
        });
    }

    private static void Cell(TableDescriptor t, string text) =>
        t.Cell().BorderBottom(1).BorderColor("#f3f4f6").PaddingVertical(3).Text(text).FontSize(8);

    /// <summary>Наработка накопителя: «6 лет (52 300 ч)». Дробные значения по-русски всегда «года».</summary>
    private static string PowerOnText(int hours)
    {
        var grouped = hours.ToString("N0", System.Globalization.CultureInfo.GetCultureInfo("ru-RU"));
        if (hours < 8760) return $"{grouped} ч";

        var years = Math.Round(hours / 8760.0, 1);
        var word = Math.Abs(years - Math.Truncate(years)) > 0.001 ? "года" : YearWord((int)years);
        var value = years.ToString("0.#", System.Globalization.CultureInfo.GetCultureInfo("ru-RU"));
        return $"{value} {word} ({grouped} ч)";
    }

    private static string YearWord(int n)
    {
        var tail100 = n % 100;
        if (tail100 is >= 11 and <= 14) return "лет";
        return (n % 10) switch { 1 => "год", 2 or 3 or 4 => "года", _ => "лет" };
    }

    private static string Uptime(DateTimeOffset boot)
    {
        var d = DateTimeOffset.UtcNow - boot;
        if (d.TotalDays >= 1) return $"{(int)d.TotalDays} дн";
        if (d.TotalHours >= 1) return $"{(int)d.TotalHours} ч";
        return $"{(int)d.TotalMinutes} мин";
    }

    private static string? Trim(string? s, int max = 60)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        return s.Length > max ? s[..max] + "…" : s;
    }

    private static List<FolderStatusDto> ParseFolders(string? reportJson)
    {
        var r = Parse<AgentReportDto>(reportJson);
        return r?.Folders ?? new();
    }

    private static T? Parse<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return default;
        try { return JsonSerializer.Deserialize<T>(json!); } catch { return default; }
    }

    /// <summary>Имя файла без кириллицы и служебных символов.</summary>
    private static string Slug(string name)
    {
        var chars = name.Select(c => char.IsLetterOrDigit(c) && c < 128 ? char.ToLowerInvariant(c) : '-').ToArray();
        var slug = new string(chars);
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        slug = slug.Trim('-');
        return slug.Length == 0 ? "client" : slug;
    }
}
