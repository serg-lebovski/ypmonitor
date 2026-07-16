using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ypmon.Server.Data;
using Ypmon.Shared;

namespace Ypmon.Server.Services;

/// <summary>
/// Фоновый мониторинг (раз в минуту), настраивается в карточке сервера:
///  • связь с агентом — если агент молчит дольше порога, «пропала связь с сервером»;
///  • ping IP-адреса — если адрес не отвечает, «на адресе пропал интернет»;
///  • свободное место на дисках — порог задаётся на каждый диск.
/// Уведомления уходят в Telegram-тему клиента только по переходам состояния
/// (упало/восстановилось) — без повторов каждую минуту.
/// </summary>
public class AvailabilityMonitor : BackgroundService
{
    private readonly IServiceProvider _sp;
    private readonly ILogger<AvailabilityMonitor> _log;

    // Ping: требуем 2 неудачных цикла подряд, чтобы одиночная потеря пакетов не поднимала тревогу.
    private readonly Dictionary<int, int> _pingFails = new();

    public AvailabilityMonitor(IServiceProvider sp, ILogger<AvailabilityMonitor> log)
    {
        _sp = sp; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(30), ct); } catch { }
        while (!ct.IsCancellationRequested)
        {
            try { await TickAsync(ct); }
            catch (Exception ex) { _log.LogError(ex, "Ошибка мониторинга доступности"); }
            try { await Task.Delay(TimeSpan.FromSeconds(60), ct); } catch { }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tg = scope.ServiceProvider.GetRequiredService<TelegramService>();
        var topics = scope.ServiceProvider.GetRequiredService<TelegramReportService>();
        var settings = await db.Settings.FirstOrDefaultAsync(ct);
        if (settings is null) return;

        var servers = await db.Servers.Include(s => s.Client)
            .Where(s => s.MonitorOfflineAlert || s.MonitorPing || s.DiskAlertsJson != null)
            .ToListAsync(ct);

        var dirty = false;
        foreach (var srv in servers)
        {
            dirty |= await CheckOfflineAsync(settings, srv, tg, topics);
            dirty |= await CheckPingAsync(settings, srv, tg, topics);
            dirty |= await CheckDisksAsync(settings, srv, tg, topics);
        }
        if (dirty) await db.SaveChangesAsync(ct);
    }

    // --- Связь с агентом -------------------------------------------------

    private async Task<bool> CheckOfflineAsync(ServerSettings s, MonitoredServer srv, TelegramService tg, TelegramReportService topics)
    {
        // Не тревожим по серверам, к которым агент ещё ни разу не подключался.
        if (!srv.MonitorOfflineAlert || srv.LastSeenAt is null) return false;

        var offline = srv.IsOffline(s.OfflineThresholdSeconds);
        if (offline == srv.AlertOfflineActive) return false;

        srv.AlertOfflineActive = offline;
        var name = WebUtility.HtmlEncode(srv.Name);
        var text = offline
            ? $"🔴 <b>{name}</b> — пропала связь с сервером: агент не выходит на связь (последний отчёт: {UiHelpers.Ago(srv.LastSeenAt)})."
            : $"🟢 <b>{name}</b> — связь с сервером восстановлена.";
        await NotifyAsync(s, srv, tg, topics, text);
        return true;
    }

    // --- Ping IP (интернет на адресе) -------------------------------------

    private async Task<bool> CheckPingAsync(ServerSettings s, MonitoredServer srv, TelegramService tg, TelegramReportService topics)
    {
        if (!srv.MonitorPing || string.IsNullOrWhiteSpace(srv.IpAddress))
        {
            _pingFails.Remove(srv.Id);
            return false;
        }

        var host = ExtractHost(srv.IpAddress!);
        var ok = await PingHostAsync(host);
        if (ok) _pingFails.Remove(srv.Id);
        else _pingFails[srv.Id] = _pingFails.GetValueOrDefault(srv.Id) + 1;

        var name = WebUtility.HtmlEncode(srv.Name);
        if (ok && srv.AlertPingActive)
        {
            srv.AlertPingActive = false;
            await NotifyAsync(s, srv, tg, topics, $"🟢 <b>{name}</b> — адрес {host} снова отвечает на ping, интернет на адресе восстановлен.");
            return true;
        }
        if (!ok && !srv.AlertPingActive && _pingFails.GetValueOrDefault(srv.Id) >= 2)
        {
            srv.AlertPingActive = true;
            await NotifyAsync(s, srv, tg, topics, $"🌐🔴 <b>{name}</b> — адрес {host} не отвечает на ping: похоже, на адресе пропал интернет.");
            return true;
        }
        return false;
    }

    /// <summary>Достаёт хост из поля «Сетевой адрес / IP» (терпит http://…, host:port, лишние пути).</summary>
    public static string ExtractHost(string raw)
    {
        raw = raw.Trim();
        if (raw.Contains("://") && Uri.TryCreate(raw, UriKind.Absolute, out var uri)) return uri.Host;
        raw = raw.Split('/', 2)[0];
        // host:port (но не IPv6 с несколькими двоеточиями)
        var idx = raw.LastIndexOf(':');
        if (idx > 0 && raw.IndexOf(':') == idx && int.TryParse(raw[(idx + 1)..], out _))
            return raw[..idx];
        return raw;
    }

    private static async Task<bool> PingHostAsync(string host)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var ping = new Ping();
                var reply = await ping.SendPingAsync(host, 3000);
                if (reply.Status == IPStatus.Success) return true;
            }
            catch { /* не резолвится / нет сети — считаем неудачей */ }
            await Task.Delay(500);
        }
        return false;
    }

    // --- Диски -------------------------------------------------------------

    private async Task<bool> CheckDisksAsync(ServerSettings s, MonitoredServer srv, TelegramService tg, TelegramReportService topics)
    {
        var thresholds = ParseThresholds(srv.DiskAlertsJson);
        if (thresholds.Count == 0 || string.IsNullOrWhiteSpace(srv.LastDisksJson)) return false;

        List<DiskStatusDto>? disks;
        try { disks = JsonSerializer.Deserialize<List<DiskStatusDto>>(srv.LastDisksJson!); }
        catch { return false; }
        if (disks is null || disks.Count == 0) return false;

        var active = ParseActive(srv.AlertDisksActiveJson);
        var name = WebUtility.HtmlEncode(srv.Name);
        var changed = false;

        foreach (var d in disks)
        {
            if (!thresholds.TryGetValue(d.Name, out var minFree) || minFree <= 0) continue;
            var freePct = d.FreePercent;

            if (freePct < minFree && !active.Contains(d.Name))
            {
                active.Add(d.Name);
                changed = true;
                await NotifyAsync(s, srv, tg, topics,
                    $"💽🔴 <b>{name}</b> — диск {d.Name} почти заполнен: свободно {freePct:0.#}% ({UiHelpers.Bytes(d.FreeBytes)} из {UiHelpers.Bytes(d.TotalBytes)}), порог {minFree}%.");
            }
            // Гистерезис +2%: не дёргаем «восстановилось/упало» на границе порога.
            else if (freePct >= minFree + 2 && active.Contains(d.Name))
            {
                active.Remove(d.Name);
                changed = true;
                await NotifyAsync(s, srv, tg, topics,
                    $"💽🟢 <b>{name}</b> — на диске {d.Name} снова достаточно места: свободно {freePct:0.#}% ({UiHelpers.Bytes(d.FreeBytes)}).");
            }
        }

        if (changed)
            srv.AlertDisksActiveJson = JsonSerializer.Serialize(active.ToList());
        return changed;
    }

    /// <summary>Пороги по дискам из JSON {"C:": 10}. Имена — без учёта регистра.</summary>
    public static Dictionary<string, int> ParseThresholds(string? json)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(json)) return result;
        try
        {
            var raw = JsonSerializer.Deserialize<Dictionary<string, int>>(json!);
            if (raw is not null)
                foreach (var (k, v) in raw)
                    if (v > 0) result[k] = Math.Clamp(v, 1, 99);
        }
        catch { }
        return result;
    }

    private static HashSet<string> ParseActive(string? json)
    {
        try
        {
            var list = string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<List<string>>(json!);
            return new HashSet<string>(list ?? new(), StringComparer.OrdinalIgnoreCase);
        }
        catch { return new HashSet<string>(StringComparer.OrdinalIgnoreCase); }
    }

    // --- Доставка ------------------------------------------------------------

    private async Task NotifyAsync(ServerSettings s, MonitoredServer srv, TelegramService tg, TelegramReportService topics, string text)
    {
        if (!s.TelegramEnabled || string.IsNullOrWhiteSpace(s.TelegramBotToken) || string.IsNullOrWhiteSpace(s.TelegramChatId))
            return; // состояние всё равно отслеживаем, чтобы после включения бота не было шквала старых тревог
        try
        {
            long? topic = srv.Client is null ? null : await topics.EnsureTopicAsync(s, srv.Client);
            var (ok, err) = await tg.SendMessageAsync(s, s.TelegramChatId!, text, topic, disablePreview: true);
            if (!ok) _log.LogWarning("Мониторинг: не удалось отправить уведомление в Telegram: {Err}", err);
        }
        catch (Exception ex) { _log.LogWarning(ex, "Мониторинг: ошибка отправки уведомления"); }
    }
}
