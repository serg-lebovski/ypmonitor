using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ypmon.Server.Data;
using Ypmon.Shared;

namespace Ypmon.Server.Services;

/// <summary>Формирование и отправка ежедневного отчёта по архивации в темы клиентов Telegram.</summary>
public class TelegramReportService
{
    private readonly AppDbContext _db;
    private readonly TelegramService _tg;
    private readonly AlertService _alerts;
    private readonly ServerLogService _logs;
    private readonly ILogger<TelegramReportService> _log;

    public TelegramReportService(AppDbContext db, TelegramService tg, AlertService alerts,
        ServerLogService logs, ILogger<TelegramReportService> log)
    {
        _db = db; _tg = tg; _alerts = alerts; _logs = logs; _log = log;
    }

    /// <summary>Гарантирует, что у клиента есть тема в группе; создаёт при необходимости.</summary>
    public async Task<long?> EnsureTopicAsync(ServerSettings s, Client client)
    {
        if (string.IsNullOrWhiteSpace(s.TelegramBotToken) || string.IsNullOrWhiteSpace(s.TelegramChatId))
            return null;
        if (client.TelegramTopicId is { } existing) return existing;

        var tid = await _tg.CreateForumTopicAsync(s, s.TelegramChatId!, client.Name);
        if (tid is not null)
        {
            client.TelegramTopicId = tid;
            await _db.SaveChangesAsync();
        }
        return tid;
    }

    /// <summary>
    /// Отправляет отчёт по всем клиентам: в группу Telegram (каждый сервер — отдельным сообщением),
    /// а также, если настроено, — на почту (общую и клиента) и в личные сообщения клиенту.
    /// </summary>
    public async Task<string> SendReportAsync()
    {
        var s = await _db.Settings.FirstOrDefaultAsync();
        if (s is null) return "Настройки не найдены.";

        var groupEnabled = s.TelegramEnabled && !string.IsNullOrWhiteSpace(s.TelegramBotToken) && !string.IsNullOrWhiteSpace(s.TelegramChatId);
        var botReady = s.TelegramEnabled && !string.IsNullOrWhiteSpace(s.TelegramBotToken);

        var clients = await _db.Clients.Include(c => c.Servers).OrderBy(c => c.Name).ToListAsync();
        var date = OmskNow().ToString("yyyy-MM-dd");
        int sent = 0, problems = 0, emails = 0, dms = 0, failed = 0;
        var globalPlain = new StringBuilder($"Отчёт по архивации за {date}\n");

        foreach (var client in clients)
        {
            var servers = client.Servers.OrderBy(x => x.Name).ToList();
            var clientPlain = new StringBuilder($"Клиент: {client.Name} — отчёт по архивации за {date}\n");
            var clientHtml = new StringBuilder($"📅 <b>{WebUtility.HtmlEncode(client.Name)}</b> — отчёт по архивации за {date}\n");
            var clientProblems = 0;

            long? topic = groupEnabled ? await EnsureTopicAsync(s, client) : null;

            if (servers.Count == 0)
            {
                clientPlain.AppendLine("У клиента нет серверов.");
                clientHtml.Append("\nУ клиента нет серверов.");
            }

            foreach (var srv in servers)
            {
                var (html, plain, problem) = BuildServerLines(s, srv);
                if (problem) { problems++; clientProblems++; }
                clientPlain.AppendLine(plain);
                clientHtml.Append('\n').Append(html);
                globalPlain.AppendLine($"[{client.Name}] {plain}");
            }

            // Одно сообщение на клиента (заголовок + все серверы), а не по сообщению на сервер.
            // Telegram ограничивает бота ~20 сообщениями в минуту в группу: при сотне клиентов
            // и сотнях серверов отчёт упирался в лимит и доходил лишь первыми сообщениями.
            if (groupEnabled)
            {
                foreach (var chunk in SplitForTelegram(clientHtml.ToString()))
                {
                    var (okSent, _) = await _tg.SendMessageAsync(s, s.TelegramChatId!, chunk, topic, disablePreview: true);
                    if (okSent) sent++; else failed++;
                }
            }

            // Дубль отчёта на почту клиента.
            if (!string.IsNullOrWhiteSpace(client.ReportEmail))
            {
                var subj = $"YPMon: архивация {client.Name} за {date}" + (clientProblems > 0 ? $" — проблемы ({clientProblems})" : " — успешно");
                if (await _alerts.SendEmailToAsync(s, client.ReportEmail, subj, clientPlain.ToString())) emails++;
            }
            // Личные сообщения клиенту от бота.
            if (botReady && !string.IsNullOrWhiteSpace(client.ReportTelegramChatId))
            {
                var (okDm, _) = await _tg.SendMessageAsync(s, client.ReportTelegramChatId!, clientHtml.ToString(), null, disablePreview: true);
                if (okDm) dms++;
            }
        }

        // Основной общий адрес — сводный отчёт по всем клиентам.
        if (!string.IsNullOrWhiteSpace(s.ArchiveReportEmailTo))
        {
            var subj = $"YPMon: отчёт по архивации за {date}" + (problems > 0 ? $" — проблемы ({problems})" : " — успешно");
            if (await _alerts.SendEmailToAsync(s, s.ArchiveReportEmailTo, subj, globalPlain.ToString())) emails++;
        }

        var summary = $"Отчёт отправлен: сообщений {sent}, писем {emails}, личных {dms}, с проблемами {problems}"
                    + (failed > 0 ? $", НЕ доставлено {failed}" : "") + ".";

        // Явная запись в журнал сервера — чтобы было видно в интерфейсе, а не только в docker logs.
        if (failed > 0)
            _logs.Warn(LogArea.Notify, "Отчёт по архивации доставлен не полностью", null, null,
                $"{summary}\nЧасть сообщений не ушла — обычно это лимит частоты Telegram. Проверьте раздел «Журнал сервера».");
        else
            _logs.Info(LogArea.Notify, "Отчёт по архивации разослан", null, null, summary);

        return summary;
    }

    /// <summary>
    /// Режет сообщение по строкам на куски под лимит Telegram (4096 символов, берём с запасом).
    /// Разрыв только по границе строк, чтобы не порвать HTML-разметку внутри строки.
    /// </summary>
    private static List<string> SplitForTelegram(string text, int max = 3500)
    {
        var parts = new List<string>();
        var sb = new StringBuilder();
        foreach (var line in text.Split('\n'))
        {
            // Одна строка длиннее лимита — отправим как есть отдельным куском (редкий случай).
            if (line.Length >= max)
            {
                if (sb.Length > 0) { parts.Add(sb.ToString()); sb.Clear(); }
                parts.Add(line[..Math.Min(line.Length, max)]);
                continue;
            }
            if (sb.Length + line.Length + 1 > max)
            {
                parts.Add(sb.ToString());
                sb.Clear();
            }
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(line);
        }
        if (sb.Length > 0) parts.Add(sb.ToString());
        return parts.Count == 0 ? new List<string> { text } : parts;
    }

    /// <summary>Строки по серверу: HTML (для Telegram) и plain (для e-mail), + признак проблемы.</summary>
    private (string html, string plain, bool problem) BuildServerLines(ServerSettings s, MonitoredServer srv)
    {
        var name = WebUtility.HtmlEncode(srv.Name);
        var offline = srv.IsOffline(s.OfflineThresholdSeconds);
        var ok = !offline && srv.LastOutcome == JobOutcome.Ok && !srv.BackupShrinkActive;
        var link = BuildLink(s, srv);

        // Нехватка места на дисках: тревога в бота приходит один раз (при уходе ниже порога),
        // а здесь напоминаем о ней в каждом ежедневном отчёте, пока место не освободили.
        var lowDisks = LowDiskDetails(srv);

        if (ok)
        {
            if (lowDisks is null)
                return ($"✅ <b>{name}</b> — архивация успешна", $"✅ {srv.Name} — архивация успешна", false);

            var htmlOk = $"⚠️ <b>{name}</b> — архивация успешна, но мало места на диске\n{WebUtility.HtmlEncode(lowDisks)}";
            if (link is not null) htmlOk += $"\n🔗 <a href=\"{link}\">открыть сервер</a>";
            var plainOk = $"⚠️ {srv.Name} — архивация успешна, но мало места на диске: {lowDisks}"
                          + (link is not null ? $" ({link})" : "");
            return (htmlOk, plainOk, true);
        }

        var details = offline
            ? $"агент офлайн (последний отчёт: {UiHelpers.Ago(srv.LastSeenAt)})"
            : srv.BackupShrinkActive
                ? $"новый файл бэкапа резко меньше предыдущего{(string.IsNullOrWhiteSpace(srv.BackupShrinkFolder) ? "" : $" (папка «{srv.BackupShrinkFolder}»)")} " +
                  $"({UiHelpers.Bytes(srv.BackupShrinkFromBytes)} → {UiHelpers.Bytes(srv.BackupShrinkToBytes)}) — проверьте"
                : ProblemDetails(srv);

        var html = $"❌ <b>{name}</b> — ошибка архивации\n{WebUtility.HtmlEncode(details)}";
        if (lowDisks is not null) html += $"\n💽 мало места: {WebUtility.HtmlEncode(lowDisks)}";
        if (link is not null) html += $"\n🔗 <a href=\"{link}\">открыть сервер</a>";
        var plain = $"❌ {srv.Name} — ошибка архивации: {details}"
                    + (lowDisks is not null ? $"; мало места: {lowDisks}" : "")
                    + (link is not null ? $" ({link})" : "");
        return (html, plain, true);
    }

    /// <summary>Диски ниже порога свободного места («C: 8% свободно (порог 15%)») или null, если всё в норме.</summary>
    private static string? LowDiskDetails(MonitoredServer srv)
    {
        if (string.IsNullOrWhiteSpace(srv.LastDisksJson)) return null;
        List<DiskStatusDto>? disks;
        try { disks = JsonSerializer.Deserialize<List<DiskStatusDto>>(srv.LastDisksJson!); }
        catch { return null; }
        if (disks is null || disks.Count == 0) return null;

        var thresholds = AvailabilityMonitor.ParseThresholds(srv.DiskAlertsJson);
        var bad = new List<string>();
        foreach (var d in disks)
        {
            if (d.TotalBytes <= 0) continue;
            var min = thresholds.TryGetValue(d.Name, out var t) ? t : AvailabilityMonitor.DefaultDiskFreePercent;
            if (min > 0 && d.FreePercent < min)
                bad.Add($"{d.Name} {d.FreePercent:0.#}% свободно ({UiHelpers.Bytes(d.FreeBytes)}), порог {min}%");
        }
        return bad.Count == 0 ? null : string.Join("; ", bad);
    }

    private static string ProblemDetails(MonitoredServer srv)
    {
        try
        {
            var r = JsonSerializer.Deserialize<AgentReportDto>(srv.LastReportJson ?? "");
            var bad = r?.Folders.Where(f => f.Outcome >= JobOutcome.Warning).Select(f => $"{f.Name}: {f.Message}").ToList();
            if (bad is { Count: > 0 }) return string.Join("; ", bad);
        }
        catch { }
        return "проблема с бэкапами (подробности на странице сервера)";
    }

    private static string? BuildLink(ServerSettings s, MonitoredServer srv)
    {
        if (string.IsNullOrWhiteSpace(s.PublicBaseUrl)) return null;
        return $"{s.PublicBaseUrl!.TrimEnd('/')}/Servers/Details?id={srv.Id}";
    }

    /// <summary>Текущее время по Омску (UTC+6, без перехода на летнее время).</summary>
    public static DateTime OmskNow() => DateTime.UtcNow.AddHours(6);
}
