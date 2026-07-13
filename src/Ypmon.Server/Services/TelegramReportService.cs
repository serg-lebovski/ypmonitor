using System.Net;
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
    private readonly ILogger<TelegramReportService> _log;

    public TelegramReportService(AppDbContext db, TelegramService tg, ILogger<TelegramReportService> log)
    {
        _db = db; _tg = tg; _log = log;
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

    /// <summary>Отправляет отчёт по всем клиентам (каждый сервер — отдельным сообщением).</summary>
    public async Task<string> SendReportAsync()
    {
        var s = await _db.Settings.FirstOrDefaultAsync();
        if (s is null || !s.TelegramEnabled || string.IsNullOrWhiteSpace(s.TelegramBotToken) || string.IsNullOrWhiteSpace(s.TelegramChatId))
            return "Telegram не настроен (токен/группа) или выключен.";

        var clients = await _db.Clients.Include(c => c.Servers).OrderBy(c => c.Name).ToListAsync();
        var date = OmskNow().ToString("yyyy-MM-dd");
        int sent = 0, problems = 0;

        foreach (var client in clients)
        {
            var topic = await EnsureTopicAsync(s, client);
            await _tg.SendMessageAsync(s, s.TelegramChatId!, $"📅 <b>Отчёт по архивации за {date}</b>", topic);

            if (client.Servers.Count == 0)
            {
                await _tg.SendMessageAsync(s, s.TelegramChatId!, "У клиента нет серверов.", topic);
                continue;
            }

            foreach (var srv in client.Servers.OrderBy(x => x.Name))
            {
                var (text, problem) = BuildServerMessage(s, srv);
                if (problem) problems++;
                var (ok, _) = await _tg.SendMessageAsync(s, s.TelegramChatId!, text, topic, disablePreview: true);
                if (ok) sent++;
            }
        }
        return $"Отчёт отправлен: сообщений {sent}, с проблемами {problems}.";
    }

    private (string text, bool problem) BuildServerMessage(ServerSettings s, MonitoredServer srv)
    {
        var name = WebUtility.HtmlEncode(srv.Name);
        var offline = srv.IsOffline(s.OfflineThresholdSeconds);
        var ok = !offline && srv.LastOutcome == JobOutcome.Ok;

        if (ok)
            return ($"✅ <b>{name}</b> — архивация успешна", false);

        var details = offline
            ? $"агент офлайн (последний отчёт: {UiHelpers.Ago(srv.LastSeenAt)})"
            : ProblemDetails(srv);

        var link = BuildLink(s, srv);
        var text = $"❌ <b>{name}</b> — ошибка архивации\n{WebUtility.HtmlEncode(details)}";
        if (link is not null) text += $"\n🔗 <a href=\"{link}\">открыть сервер</a>";
        return (text, true);
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
