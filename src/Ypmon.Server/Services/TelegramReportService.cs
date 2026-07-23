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
    /// Рассылка идёт минутами (пауза между сообщениями из-за лимита Telegram). Второй запуск
    /// в это время дал бы клиентам по два одинаковых отчёта, поэтому одновременно идёт только одна.
    /// </summary>
    private static readonly SemaphoreSlim RunGate = new(1, 1);

    /// <summary>Идёт ли рассылка прямо сейчас (для подписи на кнопке в настройках).</summary>
    public static bool IsRunning => RunGate.CurrentCount == 0;

    /// <summary>
    /// Отправляет отчёт по всем клиентам: в группу Telegram (одно сообщение на клиента),
    /// а также, если настроено, — на почту (общую и клиента) и в личные сообщения клиенту.
    /// </summary>
    public async Task<string> SendReportAsync()
    {
        if (!await RunGate.WaitAsync(0))
            return "Рассылка отчёта уже идёт — дождитесь её завершения (обычно несколько минут).";
        try { return await SendReportCoreAsync(); }
        finally { RunGate.Release(); }
    }

    /// <summary>Готовый к отправке отчёт по одному клиенту.</summary>
    private sealed record ClientReport(Client Client, string Html, string Plain, int Problems);

    private async Task<string> SendReportCoreAsync()
    {
        var s = await _db.Settings.FirstOrDefaultAsync();
        if (s is null) return "Настройки не найдены.";

        var groupEnabled = s.TelegramEnabled && !string.IsNullOrWhiteSpace(s.TelegramBotToken) && !string.IsNullOrWhiteSpace(s.TelegramChatId);
        var botReady = s.TelegramEnabled && !string.IsNullOrWhiteSpace(s.TelegramBotToken);

        var clients = await _db.Clients.Include(c => c.Servers).OrderBy(c => c.Name).ToListAsync();
        var date = OmskNow().ToString("yyyy-MM-dd");

        // Момент снимка. Весь отчёт считается «по состоянию на» него: рассылка занимает минуты,
        // и если сравнивать с текущим временем, серверы в конце очереди ложно становятся «офлайн».
        var asOf = DateTimeOffset.UtcNow;

        int sent = 0, problems = 0, emails = 0, dms = 0, failed = 0;
        var globalPlain = new StringBuilder($"Отчёт по архивации за {date}\n");
        var failedClients = new List<string>();

        // --- Фаза 1: формируем тексты по единому снимку данных (быстро, без обращений к Telegram).
        var planned = new List<ClientReport>();
        foreach (var client in clients)
        {
            var servers = client.Servers.OrderBy(x => x.Name).ToList();
            var clientPlain = new StringBuilder($"Клиент: {client.Name} — отчёт по архивации за {date}\n");
            var clientHtml = new StringBuilder($"📅 <b>{WebUtility.HtmlEncode(client.Name)}</b> — отчёт по архивации за {date}\n");
            var clientProblems = 0;

            if (servers.Count == 0)
            {
                clientPlain.AppendLine("У клиента нет серверов.");
                clientHtml.Append("\nУ клиента нет серверов.");
            }

            foreach (var srv in servers)
            {
                var (html, plain, problem) = BuildServerLines(s, srv, asOf);
                if (problem) { problems++; clientProblems++; }
                clientPlain.AppendLine(plain);
                clientHtml.Append('\n').Append(html);
                globalPlain.AppendLine($"[{client.Name}] {plain}");
            }

            planned.Add(new ClientReport(client, clientHtml.ToString(), clientPlain.ToString(), clientProblems));
        }

        _logs.Info(LogArea.Notify, "Начата рассылка отчёта по архивации", null, null,
            $"Клиентов: {planned.Count}. Сообщения идут с паузой (лимит Telegram ~20 в минуту), рассылка займёт около "
            + $"{Math.Max(1, planned.Count * 4 / 60)} мин.");

        // --- Фаза 2: отправка. Здесь уже идут паузы, но тексты зафиксированы и не «стареют».
        foreach (var item in planned)
        {
            var client = item.Client;

            // Одно сообщение на клиента (заголовок + все серверы), а не по сообщению на сервер:
            // Telegram ограничивает бота ~20 сообщениями в минуту в группу.
            if (groupEnabled)
            {
                var topic = await EnsureTopicAsync(s, client);
                foreach (var chunk in SplitForTelegram(item.Html))
                {
                    var (okSent, err) = await _tg.SendMessageAsync(s, s.TelegramChatId!, chunk, topic, disablePreview: true);

                    // Тему клиента могли удалить в Telegram — тогда заводим её заново и повторяем.
                    if (!okSent && TelegramService.IsTopicGone(err) && client.TelegramTopicId is not null)
                    {
                        _logs.Warn(LogArea.Notify, "Тема клиента в Telegram недоступна — создаём заново", null, null,
                            $"{client.Name}: {err}");
                        client.TelegramTopicId = null;
                        await _db.SaveChangesAsync();
                        topic = await EnsureTopicAsync(s, client);
                        (okSent, err) = await _tg.SendMessageAsync(s, s.TelegramChatId!, chunk, topic, disablePreview: true);
                    }

                    if (okSent) sent++;
                    else
                    {
                        failed++;
                        failedClients.Add($"{client.Name}: {err ?? "причина неизвестна"}");
                    }
                }
            }

            // Дубль отчёта на почту клиента.
            if (!string.IsNullOrWhiteSpace(client.ReportEmail))
            {
                var subj = $"YPMon: архивация {client.Name} за {date}" + (item.Problems > 0 ? $" — проблемы ({item.Problems})" : " — успешно");
                if (await _alerts.SendEmailToAsync(s, client.ReportEmail, subj, item.Plain)) emails++;
            }
            // Личные сообщения клиенту от бота.
            if (botReady && !string.IsNullOrWhiteSpace(client.ReportTelegramChatId))
            {
                var (okDm, _) = await _tg.SendMessageAsync(s, client.ReportTelegramChatId!, item.Html, null, disablePreview: true);
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
                $"{summary}\nНе доставлено:\n" + string.Join("\n", failedClients.Take(50)));
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
    private (string html, string plain, bool problem) BuildServerLines(ServerSettings s, MonitoredServer srv, DateTimeOffset asOf)
    {
        var name = WebUtility.HtmlEncode(srv.Name);
        var offline = srv.IsOffline(s.OfflineThresholdSeconds, asOf);
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
            ? $"агент офлайн (последний отчёт: {UiHelpers.Ago(srv.LastSeenAt, asOf)})"
            : srv.BackupShrinkActive
                ? $"новый файл бэкапа резко меньше предыдущего{(string.IsNullOrWhiteSpace(srv.BackupShrinkFolder) ? "" : $" (папка «{srv.BackupShrinkFolder}»)")} " +
                  $"({UiHelpers.Bytes(srv.BackupShrinkFromBytes)} → {UiHelpers.Bytes(srv.BackupShrinkToBytes)}) — проверьте"
                : ProblemDetails(srv);

        // Заглушённый сервер остаётся в отчёте, но помечается — чтобы было видно, что тревоги временно молчат.
        var mute = AlertSuppression.MuteReason(srv, asOf);

        var html = $"❌ <b>{name}</b> — ошибка архивации\n{WebUtility.HtmlEncode(details)}";
        if (lowDisks is not null) html += $"\n💽 мало места: {WebUtility.HtmlEncode(lowDisks)}";
        if (mute is not null) html += $"\n🔇 {WebUtility.HtmlEncode(mute)}";
        if (link is not null) html += $"\n🔗 <a href=\"{link}\">открыть сервер</a>";
        var plain = $"❌ {srv.Name} — ошибка архивации: {details}"
                    + (lowDisks is not null ? $"; мало места: {lowDisks}" : "")
                    + (mute is not null ? $"; 🔇 {mute}" : "")
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
