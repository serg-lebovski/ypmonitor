using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Ypmon.Server.Data;
using Ypmon.Server.Services;
using Ypmon.Shared;

namespace Ypmon.Server.Pages;

public class ReportsModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly ClientReportPdfService _pdf;
    private readonly AuditService _audit;
    public ReportsModel(AppDbContext db, ClientReportPdfService pdf, AuditService audit)
    {
        _db = db; _pdf = pdf; _audit = audit;
    }

    public List<Client> Clients { get; set; } = new();

    public int TotalClients => Clients.Count;
    public int TotalServers => Clients.Sum(c => c.Servers.Count);

    // --- Статистика проблем за период (для блока «Статистика проблем») ---
    public string StatsPeriod { get; set; } = "this-month";
    public DateTimeOffset StatsFrom { get; set; }
    public DateTimeOffset StatsTo { get; set; }
    public List<ServerProblemRow> ServerProblems { get; set; } = new();
    public List<(string Category, int Count)> TopProblems { get; set; } = new();

    public record ServerProblemRow(int ServerId, string Server, string Client, int ErrorReports, int WarningReports, List<(string Category, int Count)> Categories);

    public async Task OnGetAsync(string? statsPeriod)
    {
        Clients = await _db.Clients.Include(c => c.Servers).OrderBy(c => c.Name).ToListAsync();
        foreach (var c in Clients)
            c.Servers = c.Servers.OrderBy(s => s.Name).ToList();
        await LoadProblemStatsAsync(statsPeriod);
    }

    /// <summary>
    /// Статистика проблем архивации и дисков по каждому серверу за период — на основе истории
    /// полных отчётов (Report, раз в 6 ч, хранится ReportRetentionDays). Проблемы SMART/CPU/офлайн
    /// сюда не входят: по ним нет накопленной истории переходов (тревога шлётся, только когда
    /// впервые срабатывает, а не логируется каждый раз отдельной записью).
    /// </summary>
    private async Task LoadProblemStatsAsync(string? period)
    {
        StatsPeriod = period is "prev-month" or "30d" ? period : "this-month";
        var omskNow = DateTimeOffset.UtcNow.AddHours(6);
        DateTimeOffset fromOmsk, toOmsk;
        switch (StatsPeriod)
        {
            case "prev-month":
                fromOmsk = Month(omskNow.AddMonths(-1));
                toOmsk = Month(omskNow).AddSeconds(-1);
                break;
            case "30d":
                fromOmsk = omskNow.AddDays(-30);
                toOmsk = omskNow;
                break;
            default:
                fromOmsk = Month(omskNow);
                toOmsk = omskNow;
                break;
        }
        StatsFrom = fromOmsk;
        StatsTo = toOmsk;
        var fromUtc = fromOmsk.AddHours(-6);
        var toUtc = toOmsk.AddHours(-6);

        var serverInfo = new Dictionary<int, (string Name, string Client, string? DiskAlertsJson)>();
        foreach (var c in Clients)
            foreach (var s in c.Servers)
                serverInfo[s.Id] = (s.Name, c.Name, s.DiskAlertsJson);

        // SQLite не умеет сравнивать DateTimeOffset в SQL — фильтруем по периоду в памяти.
        // Unknown — не проблема, а «нет данных» (например, у сервера не настроены папки архивации).
        var badReports = await _db.Reports
            .Where(r => r.Outcome == JobOutcome.Error || r.Outcome == JobOutcome.Warning)
            .ToListAsync();
        var inPeriod = badReports.Where(r => r.ReceivedAt >= fromUtc && r.ReceivedAt <= toUtc);

        var byServer = new Dictionary<int, (int err, int warn, Dictionary<string, int> cats)>();
        var globalCats = new Dictionary<string, int>();

        foreach (var r in inPeriod)
        {
            if (!serverInfo.TryGetValue(r.ServerId, out var info)) continue;
            if (!byServer.TryGetValue(r.ServerId, out var acc))
                acc = (0, 0, new Dictionary<string, int>());
            if (r.Outcome == JobOutcome.Error) acc.err++;
            else if (r.Outcome == JobOutcome.Warning) acc.warn++;

            AgentReportDto? payload = null;
            try { payload = JsonSerializer.Deserialize<AgentReportDto>(r.PayloadJson); } catch { /* старый/битый payload — пропускаем */ }
            if (payload is not null)
            {
                foreach (var f in payload.Folders.Where(f => f.Outcome >= JobOutcome.Warning))
                {
                    var cat = CategorizeFolder(f);
                    Bump(acc.cats, cat);
                    Bump(globalCats, cat);
                }
                if (payload.Disks.Count > 0)
                {
                    var thresholds = AvailabilityMonitor.ParseThresholds(info.DiskAlertsJson);
                    foreach (var d in payload.Disks)
                    {
                        var minFree = thresholds.TryGetValue(d.Name, out var t) ? t : AvailabilityMonitor.DefaultDiskFreePercent;
                        if (minFree > 0 && d.FreePercent < minFree)
                        {
                            Bump(acc.cats, "Мало места на диске");
                            Bump(globalCats, "Мало места на диске");
                        }
                    }
                }
            }
            byServer[r.ServerId] = acc;
        }

        ServerProblems = byServer
            .Select(kv => new ServerProblemRow(
                kv.Key, serverInfo[kv.Key].Name, serverInfo[kv.Key].Client,
                kv.Value.err, kv.Value.warn,
                kv.Value.cats.OrderByDescending(c => c.Value).Select(c => (c.Key, c.Value)).ToList()))
            .OrderByDescending(x => x.ErrorReports + x.WarningReports)
            .ToList();

        TopProblems = globalCats.OrderByDescending(c => c.Value).Select(c => (c.Key, c.Value)).Take(10).ToList();

        static void Bump(Dictionary<string, int> d, string key) => d[key] = d.TryGetValue(key, out var v) ? v + 1 : 1;
        static DateTimeOffset Month(DateTimeOffset t) => new(t.Year, t.Month, 1, 0, 0, 0, t.Offset);
        static string CategorizeFolder(FolderStatusDto f)
        {
            if (!f.Accessible) return "Папка недоступна";
            if (f.FileCount == 0) return "Нет файлов в папке";
            return "Устаревший бэкап (нет новых файлов)";
        }
    }

    /// <summary>
    /// Отчёт по обслуживанию для клиента (PDF) — по всем его серверам за выбранный период.
    /// Отдаём файл сразу на скачивание: хранить его на сервере незачем.
    /// </summary>
    public async Task<IActionResult> OnPostPdfAsync(int clientId, string? period)
    {
        if (!User.CanEdit()) return Forbid();

        var omskNow = DateTimeOffset.UtcNow.AddHours(6);
        // Границы периода считаем по Омску, но храним в UTC — как и всё остальное время в базе.
        var (from, to) = period switch
        {
            "prev-month" => (Month(omskNow.AddMonths(-1)), Month(omskNow).AddSeconds(-1)),
            "this-month" => (Month(omskNow), omskNow),
            _ => (omskNow.AddDays(-30), omskNow),
        };

        var result = await _pdf.BuildAsync(clientId, from.AddHours(-6), to.AddHours(-6));
        if (result is null) return NotFound();

        await _audit.LogAsync(User, "Сформирован отчёт по клиенту", $"клиент #{clientId}, период {period ?? "30 дней"}");
        return File(result.Value.pdf, "application/pdf", result.Value.fileName);

        static DateTimeOffset Month(DateTimeOffset t) => new(t.Year, t.Month, 1, 0, 0, 0, t.Offset);
    }

    /// <summary>
    /// CSV-выгрузка таблицы адресов клиентов. Разделитель — ";", а не ",": так CSV открывается
    /// в Excel с русской локалью без плясок с настройками импорта. BOM в начале — чтобы Excel
    /// понял, что файл в UTF-8, и не превратил кириллицу в кракозябры.
    /// </summary>
    public async Task<IActionResult> OnGetAddressesCsvAsync()
    {
        var clients = await _db.Clients.Include(c => c.Servers).OrderBy(c => c.Name).ToListAsync();

        var sb = new StringBuilder();
        sb.Append("Клиент;Сервер;Физический адрес;Локальный IP;Внешний IP;Провайдер\r\n");
        foreach (var c in clients)
            foreach (var s in c.Servers.OrderBy(x => x.Name))
                sb.Append(string.Join(';', new[] { c.Name, s.Name, s.PhysicalAddress, s.IpAddress, s.ExternalIpAddress, s.IspProvider }
                    .Select(CsvEscape))).Append("\r\n");

        var bom = new byte[] { 0xEF, 0xBB, 0xBF };
        var body = Encoding.UTF8.GetBytes(sb.ToString());
        var bytes = new byte[bom.Length + body.Length];
        bom.CopyTo(bytes, 0);
        body.CopyTo(bytes, bom.Length);

        var fileName = $"ypmon-addresses-{DateTimeOffset.UtcNow.AddHours(6):yyyy-MM-dd}.csv";
        return File(bytes, "text/csv", fileName);

        static string CsvEscape(string? v)
        {
            v ??= "";
            return v.IndexOfAny(new[] { ';', '"', '\n', '\r' }) >= 0
                ? "\"" + v.Replace("\"", "\"\"") + "\""
                : v;
        }
    }
}
