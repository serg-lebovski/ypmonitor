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
    private readonly ILogger<TelegramReportService> _log;

    public TelegramReportService(AppDbContext db, TelegramService tg, AlertService alerts, ILogger<TelegramReportService> log)
    {
        _db = db; _tg = tg; _alerts = alerts; _log = log;
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
        int sent = 0, problems = 0, emails = 0, dms = 0;
        var globalPlain = new StringBuilder($"Отчёт по архивации за {date}\n");

        foreach (var client in clients)
        {
            var servers = client.Servers.OrderBy(x => x.Name).ToList();
            var clientPlain = new StringBuilder($"Клиент: {client.Name} — отчёт по архивации за {date}\n");
            var clientHtml = new StringBuilder($"📅 <b>{WebUtility.HtmlEncode(client.Name)}</b> — отчёт по архивации за {date}\n");
            var clientProblems = 0;

            long? topic = groupEnabled ? await EnsureTopicAsync(s, client) : null;
            if (groupEnabled)
                await _tg.SendMessageAsync(s, s.TelegramChatId!, $"📅 <b>Отчёт по архивации за {date}</b>", topic);

            if (servers.Count == 0)
            {
                if (groupEnabled) await _tg.SendMessageAsync(s, s.TelegramChatId!, "У клиента нет серверов.", topic);
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
                if (groupEnabled)
                {
                    var (okSent, _) = await _tg.SendMessageAsync(s, s.TelegramChatId!, html, topic, disablePreview: true);
                    if (okSent) sent++;
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

        return $"Отчёт отправлен: сообщений {sent}, писем {emails}, личных {dms}, с проблемами {problems}.";
    }

    /// <summary>Строки по серверу: HTML (для Telegram) и plain (для e-mail), + признак проблемы.</summary>
    private (string html, string plain, bool problem) BuildServerLines(ServerSettings s, MonitoredServer srv)
    {
        var name = WebUtility.HtmlEncode(srv.Name);
        var offline = srv.IsOffline(s.OfflineThresholdSeconds);
        var ok = !offline && srv.LastOutcome == JobOutcome.Ok && !srv.BackupShrinkActive;

        if (ok)
            return ($"✅ <b>{name}</b> — архивация успешна", $"✅ {srv.Name} — архивация успешна", false);

        var details = offline
            ? $"агент офлайн (последний отчёт: {UiHelpers.Ago(srv.LastSeenAt)})"
            : srv.BackupShrinkActive
                ? $"объём бэкапа резко уменьшился ({UiHelpers.Bytes(srv.BackupShrinkFromBytes)} → {UiHelpers.Bytes(srv.BackupShrinkToBytes)}) — проверьте"
                : ProblemDetails(srv);

        var link = BuildLink(s, srv);
        var html = $"❌ <b>{name}</b> — ошибка архивации\n{WebUtility.HtmlEncode(details)}";
        if (link is not null) html += $"\n🔗 <a href=\"{link}\">открыть сервер</a>";
        var plain = $"❌ {srv.Name} — ошибка архивации: {details}" + (link is not null ? $" ({link})" : "");
        return (html, plain, true);
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
