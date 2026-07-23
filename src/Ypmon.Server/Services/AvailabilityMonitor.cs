using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ypmon.Server.Data;
using Ypmon.Shared;

namespace Ypmon.Server.Services;

/// <summary>
/// Фоновый мониторинг (цикл каждые 15 сек), настраивается в карточках сервера/клиента:
///  • связь с агентом — порог офлайна адаптивный (2×heartbeat-интервал агента + запас),
///    поэтому «пропала связь» ловится сразу, с поправкой на настроенную агентом частоту;
///  • ping IP сервера — с индивидуальным интервалом проверки (карточка сервера);
///  • ping роутера клиента — у клиента может не быть серверов, но роутер пингуется;
///  • свободное место на дисках — порог задаётся на каждый диск.
/// Уведомления уходят в Telegram-тему клиента только по переходам состояния
/// (упало/восстановилось) — без повторов.
/// </summary>
public class AvailabilityMonitor : BackgroundService
{
    /// <summary>Порог свободного места на диске по умолчанию, % (если для диска не задан свой).</summary>
    public const int DefaultDiskFreePercent = 15;

    private readonly IServiceProvider _sp;
    private readonly ServerLogService _logs;
    private readonly ILogger<AvailabilityMonitor> _log;

    // Ping: требуем 2 неудачных проверки подряд, чтобы одиночная потеря пакетов не поднимала тревогу.
    private readonly Dictionary<string, int> _pingFails = new();
    // Когда наступает следующая проверка ping (ключи: "s<id>" — сервер, "c<id>" — роутер клиента).
    private readonly Dictionary<string, DateTimeOffset> _nextDue = new();

    public AvailabilityMonitor(IServiceProvider sp, ServerLogService logs, ILogger<AvailabilityMonitor> log)
    {
        _sp = sp; _logs = logs; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(30), ct); } catch { }
        while (!ct.IsCancellationRequested)
        {
            try { await TickAsync(ct); }
            catch (Exception ex) { _log.LogError(ex, "Ошибка мониторинга доступности"); }
            try { await Task.Delay(TimeSpan.FromSeconds(15), ct); } catch { }
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

        // Диски проверяем у всех, кто прислал их (порог по умолчанию 15% работает и без явной настройки).
        var servers = await db.Servers.Include(s => s.Client)
            .Where(s => s.MonitorOfflineAlert || s.MonitorPing || s.DiskAlertsJson != null
                     || s.LastDisksJson != null || s.LastPhysicalDisksJson != null)
            .ToListAsync(ct);

        var dirty = false;
        foreach (var srv in servers)
        {
            dirty |= await CheckOfflineAsync(settings, srv, tg, topics);
            dirty |= await CheckPingAsync(settings, srv, tg, topics);
            dirty |= await CheckDisksAsync(settings, srv, tg, topics);
            dirty |= await CheckDiskHealthAsync(settings, srv, tg, topics);
        }

        // Роутеры клиентов: пингуем всех, у кого задан адрес (даже если серверов нет).
        var clients = await db.Clients
            .Where(c => c.RouterAddress != null && c.RouterAddress != "")
            .ToListAsync(ct);
        foreach (var client in clients)
            dirty |= await CheckRouterAsync(settings, client, tg, topics);

        if (dirty) await db.SaveChangesAsync(ct);
    }

    /// <summary>Пора ли выполнять периодическую проверку с данным интервалом.</summary>
    private bool Due(string key, int intervalSeconds)
    {
        var now = DateTimeOffset.UtcNow;
        if (_nextDue.TryGetValue(key, out var next) && now < next) return false;
        _nextDue[key] = now.AddSeconds(Math.Max(10, intervalSeconds));
        return true;
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
        await NotifyServerAsync(s, srv, AlertKind.Offline, tg, topics, text);
        return true;
    }

    // --- Ping IP сервера (интернет на адресе) ------------------------------

    private async Task<bool> CheckPingAsync(ServerSettings s, MonitoredServer srv, TelegramService tg, TelegramReportService topics)
    {
        var key = "s" + srv.Id;
        // Мониторинг доступности интернета привязан к ВНЕШНЕМУ IP сервера (роутеру).
        if (!srv.MonitorPing || string.IsNullOrWhiteSpace(srv.ExternalIpAddress))
        {
            _pingFails.Remove(key);
            return false;
        }
        if (!Due(key, srv.PingIntervalSeconds)) return false;

        var host = ExtractHost(srv.ExternalIpAddress!);
        var ok = await PingHostAsync(host);
        if (ok) _pingFails.Remove(key);
        else _pingFails[key] = _pingFails.GetValueOrDefault(key) + 1;

        var name = WebUtility.HtmlEncode(srv.Name);
        if (ok && srv.AlertPingActive)
        {
            srv.AlertPingActive = false;
            await NotifyServerAsync(s, srv, AlertKind.Ping, tg, topics, $"🟢 <b>{name}</b> — адрес {host} снова отвечает на ping, интернет на адресе восстановлен.");
            return true;
        }
        if (!ok && !srv.AlertPingActive && _pingFails.GetValueOrDefault(key) >= 2)
        {
            srv.AlertPingActive = true;
            await NotifyServerAsync(s, srv, AlertKind.Ping, tg, topics, $"🌐🔴 <b>{name}</b> — адрес {host} не отвечает на ping: похоже, на адресе пропал интернет.");
            return true;
        }
        return false;
    }

    // --- Ping роутера клиента ----------------------------------------------

    private async Task<bool> CheckRouterAsync(ServerSettings s, Client client, TelegramService tg, TelegramReportService topics)
    {
        var key = "c" + client.Id;
        if (!Due(key, client.RouterPingIntervalSeconds)) return false;

        var host = ExtractHost(client.RouterAddress!);
        var ok = await PingHostAsync(host);
        if (ok) _pingFails.Remove(key);
        else _pingFails[key] = _pingFails.GetValueOrDefault(key) + 1;

        var dirty = client.LastRouterPingOk != ok || client.LastRouterPingAt is null;
        client.LastRouterPingOk = ok;
        client.LastRouterPingAt = DateTimeOffset.UtcNow;

        var name = WebUtility.HtmlEncode(client.Name);
        if (ok && client.AlertRouterPingActive)
        {
            client.AlertRouterPingActive = false;
            await NotifyAsync(s, client, tg, topics, $"🟢 <b>{name}</b> — роутер {host} снова отвечает на ping.");
            return true;
        }
        if (!ok && !client.AlertRouterPingActive && _pingFails.GetValueOrDefault(key) >= 2)
        {
            client.AlertRouterPingActive = true;
            await NotifyAsync(s, client, tg, topics, $"🌐🔴 <b>{name}</b> — роутер {host} не отвечает на ping.");
            return true;
        }
        return dirty;
    }

    /// <summary>Достаёт хост из поля адреса (терпит http://…, host:port, лишние пути).</summary>
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
        if (string.IsNullOrWhiteSpace(srv.LastDisksJson)) return false;
        var thresholds = ParseThresholds(srv.DiskAlertsJson);

        List<DiskStatusDto>? disks;
        try { disks = JsonSerializer.Deserialize<List<DiskStatusDto>>(srv.LastDisksJson!); }
        catch { return false; }
        if (disks is null || disks.Count == 0) return false;

        var active = ParseActive(srv.AlertDisksActiveJson);
        var name = WebUtility.HtmlEncode(srv.Name);
        var changed = false;

        foreach (var d in disks)
        {
            // Порог: свой для диска, иначе — значение по умолчанию (15%). 0 = отключён.
            var minFree = thresholds.TryGetValue(d.Name, out var t) ? t : DefaultDiskFreePercent;
            if (minFree <= 0)
            {
                if (active.Remove(d.Name)) changed = true;   // отключили порог — снимаем тревогу молча
                continue;
            }
            var freePct = d.FreePercent;

            if (freePct < minFree && !active.Contains(d.Name))
            {
                active.Add(d.Name);
                changed = true;
                await NotifyServerAsync(s, srv, AlertKind.DiskLow, tg, topics,
                    $"💽🔴 <b>{name}</b> — диск {d.Name} почти заполнен: свободно {freePct:0.#}% ({UiHelpers.Bytes(d.FreeBytes)} из {UiHelpers.Bytes(d.TotalBytes)}), порог {minFree}%.");
            }
            // Гистерезис +2%: не дёргаем «восстановилось/упало» на границе порога.
            else if (freePct >= minFree + 2 && active.Contains(d.Name))
            {
                active.Remove(d.Name);
                changed = true;
                await NotifyServerAsync(s, srv, AlertKind.DiskLow, tg, topics,
                    $"💽🟢 <b>{name}</b> — на диске {d.Name} снова достаточно места: свободно {freePct:0.#}% ({UiHelpers.Bytes(d.FreeBytes)}).");
            }
        }

        if (changed)
            srv.AlertDisksActiveJson = JsonSerializer.Serialize(active.ToList());
        return changed;
    }

    // --- Здоровье физических дисков (SMART) --------------------------------

    private static string HealthRu(string? h) => h switch
    {
        "Healthy" => "Здоров", "Warning" => "Предупреждение", "Unhealthy" => "Неисправен", _ => "нет данных"
    };
    /// <summary>Ранг здоровья для сравнения (больше — хуже). Warning учитывается только если включено.</summary>
    private static int HealthRank(string? h, bool alertOnWarning) => h switch
    {
        "Unhealthy" => 2,
        "Warning" => alertOnWarning ? 1 : 0,
        _ => 0
    };

    private async Task<bool> CheckDiskHealthAsync(ServerSettings s, MonitoredServer srv, TelegramService tg, TelegramReportService topics)
    {
        if (string.IsNullOrWhiteSpace(srv.LastPhysicalDisksJson)) return false;
        List<PhysicalDiskDto>? disks;
        try { disks = JsonSerializer.Deserialize<List<PhysicalDiskDto>>(srv.LastPhysicalDisksJson!); }
        catch { return false; }
        if (disks is null || disks.Count == 0) return false;

        var onWarn = s.SmartAlertOnWarning;
        var tempTh = s.SmartTempThresholdC;

        // Прошлое здоровье по каждому накопителю (ключ — серийник, иначе имя) и множество «перегретых».
        Dictionary<string, string> prev;
        try
        {
            prev = string.IsNullOrWhiteSpace(srv.DiskHealthStateJson)
                ? new() : JsonSerializer.Deserialize<Dictionary<string, string>>(srv.DiskHealthStateJson!) ?? new();
        }
        catch { prev = new(); }
        var hot = ParseActive(srv.DiskTempAlertJson);

        var next = new Dictionary<string, string>();
        var worsened = new List<string>();
        var improved = new List<string>();
        var tempWorse = new List<string>();
        var tempOk = new List<string>();
        var tempChanged = false;

        foreach (var d in disks)
        {
            var key = string.IsNullOrWhiteSpace(d.Serial) ? d.Name : d.Serial!;
            if (string.IsNullOrWhiteSpace(key)) continue;
            next[key] = d.Health;

            // --- Здоровье ---
            var prevHealth = prev.TryGetValue(key, out var ph) ? ph : "Healthy";  // новый диск считаем «был здоров»
            var cur = HealthRank(d.Health, onWarn);
            var was = HealthRank(prevHealth, onWarn);
            if (cur > was)
                worsened.Add($"{WebUtility.HtmlEncode(d.Name)}: {HealthRu(prevHealth)} → <b>{HealthRu(d.Health)}</b>"
                    + (d.TemperatureC is int tw ? $" ({tw} °C)" : ""));
            else if (cur < was)
                improved.Add($"{WebUtility.HtmlEncode(d.Name)}: {HealthRu(prevHealth)} → {HealthRu(d.Health)}");

            // --- Температура (порог с гистерезисом 3 °C) ---
            if (tempTh > 0 && d.TemperatureC is int tc)
            {
                var wasHot = hot.Contains(key);
                if (!wasHot && tc > tempTh)
                {
                    hot.Add(key); tempChanged = true;
                    tempWorse.Add($"{WebUtility.HtmlEncode(d.Name)}: {tc} °C (порог {tempTh} °C)");
                }
                else if (wasHot && tc <= tempTh - 3)
                {
                    hot.Remove(key); tempChanged = true;
                    tempOk.Add($"{WebUtility.HtmlEncode(d.Name)}: {tc} °C");
                }
            }
        }

        // Убираем из «перегретых» накопители, которых больше нет в отчёте.
        foreach (var k in hot.ToList())
            if (!next.ContainsKey(k)) { hot.Remove(k); tempChanged = true; }

        var healthChanged = !SameState(prev, next);
        if (healthChanged) srv.DiskHealthStateJson = JsonSerializer.Serialize(next);
        if (tempChanged) srv.DiskTempAlertJson = hot.Count == 0 ? null : JsonSerializer.Serialize(hot.ToList());
        srv.AlertDiskHealthActive = disks.Any(d => d.Health is "Warning" or "Unhealthy") || hot.Count > 0;

        // Уведомления шлём только если SMART-оповещения включены (состояние копим в любом случае — без спама после включения).
        if (s.SmartAlertsEnabled)
        {
            var name = WebUtility.HtmlEncode(srv.Name);
            if (worsened.Count > 0)
                await NotifyServerAsync(s, srv, AlertKind.DiskHealth, tg, topics,
                    $"🩺🔴 <b>{name}</b> — ухудшились показатели SMART:\n{string.Join("\n", worsened.Select(x => "• " + x))}\nСтоит проверить накопитель и бэкапы.");
            if (tempWorse.Count > 0)
                await NotifyServerAsync(s, srv, AlertKind.DiskHealth, tg, topics,
                    $"🌡️🔴 <b>{name}</b> — перегрев накопителя:\n{string.Join("\n", tempWorse.Select(x => "• " + x))}");
            if (improved.Count > 0)
                await NotifyServerAsync(s, srv, AlertKind.DiskHealth, tg, topics,
                    $"🩺🟢 <b>{name}</b> — здоровье накопителя улучшилось:\n{string.Join("\n", improved.Select(x => "• " + x))}");
            if (tempOk.Count > 0)
                await NotifyServerAsync(s, srv, AlertKind.DiskHealth, tg, topics,
                    $"🌡️🟢 <b>{name}</b> — температура накопителя в норме:\n{string.Join("\n", tempOk.Select(x => "• " + x))}");
        }

        return healthChanged || tempChanged;
    }

    private static bool SameState(Dictionary<string, string> a, Dictionary<string, string> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var (k, v) in b)
            if (!a.TryGetValue(k, out var av) || av != v) return false;
        return true;
    }

    /// <summary>
    /// Пороги по дискам из JSON {"C:": 10}. Имена — без учёта регистра. Значение 0 сохраняется —
    /// это «порог отключён» (иначе диск подпадал бы под значение по умолчанию 15%).
    /// </summary>
    public static Dictionary<string, int> ParseThresholds(string? json)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(json)) return result;
        try
        {
            var raw = JsonSerializer.Deserialize<Dictionary<string, int>>(json!);
            if (raw is not null)
                foreach (var (k, v) in raw)
                    result[k] = Math.Clamp(v, 0, 99);
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

    /// <summary>
    /// Отправка тревоги по конкретному серверу — с учётом тихого режима. Если сервер заглушён
    /// (snooze) или идёт окно обслуживания, уведомление НЕ отправляется, но факт пишется в журнал,
    /// чтобы заглушённое не пропало бесследно. Состояние тревоги на сервере при этом отслеживается
    /// как обычно (флаги уже выставлены вызывающим).
    /// </summary>
    private async Task NotifyServerAsync(ServerSettings s, MonitoredServer srv, AlertKind kind,
        TelegramService tg, TelegramReportService topics, string text)
    {
        if (AlertSuppression.IsSuppressed(srv, kind, DateTimeOffset.UtcNow, out var reason))
        {
            _logs.Info(LogArea.Notify, $"Тревога заглушена ({reason})", srv.Name, null,
                System.Text.RegularExpressions.Regex.Replace(text, "<.*?>", ""));
            return;
        }
        await NotifyAsync(s, srv.Client, tg, topics, text);
    }

    private async Task NotifyAsync(ServerSettings s, Client? client, TelegramService tg, TelegramReportService topics, string text)
    {
        if (!s.TelegramEnabled || string.IsNullOrWhiteSpace(s.TelegramBotToken) || string.IsNullOrWhiteSpace(s.TelegramChatId))
            return; // состояние всё равно отслеживаем, чтобы после включения бота не было шквала старых тревог
        try
        {
            long? topic = client is null ? null : await topics.EnsureTopicAsync(s, client);
            var (ok, err) = await tg.SendMessageAsync(s, s.TelegramChatId!, text, topic, disablePreview: true);
            if (!ok) _log.LogWarning("Мониторинг: не удалось отправить уведомление в Telegram: {Err}", err);
        }
        catch (Exception ex) { _log.LogWarning(ex, "Мониторинг: ошибка отправки уведомления"); }
    }
}
