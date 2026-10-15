using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ypmon.Server.Data;
using Ypmon.Shared;

namespace Ypmon.Server.Services;

/// <summary>
/// Заводит инциденты в GLPI как Project/ProjectTask (а не Ticket) — так они не попадают в общий
/// пул заявок, а лежат отдельным деревом: Проект «Мониторинг серверов» → задача-категория по типу
/// проблемы (одна на все клиенты) → подзадача-инцидент на конкретный сервер.
///
/// Сессия GLPI не держится между вызовами (как и остальные интеграции в проекте — просто и надёжно):
/// на каждую операцию — initSession/killSession. Все ошибки (GLPI недоступен, протух токен, не хватает
/// прав) ловятся внутри и пишутся в журнал сервера — мониторинг и рассылка тревог из-за них не падают.
/// </summary>
public class GlpiService
{
    private readonly AppDbContext _db;
    private readonly ServerLogService _logs;
    private readonly ILogger<GlpiService> _log;

    public GlpiService(AppDbContext db, ServerLogService logs, ILogger<GlpiService> log)
    {
        _db = db; _logs = logs; _log = log;
    }

    /// <summary>Названия родительских задач-категорий — согласованы с заказчиком на демо-проекте.</summary>
    private static readonly Dictionary<AlertKind, string> CategoryNames = new()
    {
        [AlertKind.Offline] = "Агент не доступен",
        [AlertKind.Ping] = "Не отвечает ping",
        [AlertKind.DiskLow] = "Мало места на диске",
        [AlertKind.DiskMissing] = "Диск пропал",
        [AlertKind.DiskHealth] = "Диск (SMART/перегрев)",
        [AlertKind.CpuOverheat] = "Перегрев процессора",
        [AlertKind.Backup] = "Ошибка архивации",
        [AlertKind.BackupShrink] = "Усадка бэкапа",
    };

    private static bool Configured(ServerSettings s) =>
        s.GlpiEnabled && !string.IsNullOrWhiteSpace(s.GlpiUrl)
        && !string.IsNullOrWhiteSpace(s.GlpiAppToken) && !string.IsNullOrWhiteSpace(s.GlpiUserToken)
        && s.GlpiProjectId > 0;

    private HttpClient CreateClient() => new() { Timeout = TimeSpan.FromSeconds(20) };

    // --- Низкоуровневые вызовы API ------------------------------------------------------------

    private async Task<string?> InitSessionAsync(HttpClient http, ServerSettings s)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, s.GlpiUrl!.TrimEnd('/') + "/initSession");
        req.Headers.Add("Authorization", "user_token " + s.GlpiUserToken);
        req.Headers.Add("App-Token", s.GlpiAppToken);
        var resp = await http.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"initSession: {resp.StatusCode} {body}");
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("session_token", out var t) ? t.GetString() : null;
    }

    private async Task KillSessionAsync(HttpClient http, ServerSettings s, string sessionToken)
    {
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get, s.GlpiUrl!.TrimEnd('/') + "/killSession");
            req.Headers.Add("Session-Token", sessionToken);
            req.Headers.Add("App-Token", s.GlpiAppToken);
            await http.SendAsync(req);
        }
        catch { /* сессия и так живёт недолго — не критично */ }
    }

    private static void AddAuth(HttpRequestMessage req, ServerSettings s, string sessionToken)
    {
        req.Headers.Add("Session-Token", sessionToken);
        req.Headers.Add("App-Token", s.GlpiAppToken);
    }

    /// <summary>POST ProjectTask или Project. Возвращает id нового объекта.</summary>
    private async Task<int> CreateAsync(HttpClient http, ServerSettings s, string sessionToken, string itemType, object input)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"{s.GlpiUrl!.TrimEnd('/')}/{itemType}");
        AddAuth(req, s, sessionToken);
        req.Content = new StringContent(JsonSerializer.Serialize(new { input }), Encoding.UTF8, "application/json");
        var resp = await http.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"POST {itemType}: {resp.StatusCode} {body}");
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("id").GetInt32();
    }

    private async Task UpdateAsync(HttpClient http, ServerSettings s, string sessionToken, string itemType, int id, object input)
    {
        var req = new HttpRequestMessage(HttpMethod.Put, $"{s.GlpiUrl!.TrimEnd('/')}/{itemType}/{id}");
        AddAuth(req, s, sessionToken);
        req.Content = new StringContent(JsonSerializer.Serialize(new { input }), Encoding.UTF8, "application/json");
        var resp = await http.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"PUT {itemType}/{id}: {resp.StatusCode} {body}");
    }

    // --- Проверка соединения (кнопка в настройках) --------------------------------------------

    public async Task<(bool ok, string message)> TestConnectionAsync(ServerSettings s)
    {
        if (!s.GlpiEnabled)
            return (false, "Галочка «Заводить инциденты в GLPI» не отмечена — отметьте и сохраните ещё раз.");
        if (string.IsNullOrWhiteSpace(s.GlpiUrl) || string.IsNullOrWhiteSpace(s.GlpiAppToken)
            || string.IsNullOrWhiteSpace(s.GlpiUserToken) || s.GlpiProjectId <= 0)
            return (false, "Заполните URL, оба токена и ID проекта.");
        using var http = CreateClient();
        string? token = null;
        try
        {
            token = await InitSessionAsync(http, s);
            if (token is null) return (false, "GLPI не вернул session_token.");

            var req = new HttpRequestMessage(HttpMethod.Get, $"{s.GlpiUrl!.TrimEnd('/')}/Project/{s.GlpiProjectId}");
            AddAuth(req, s, token);
            var resp = await http.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode) return (false, $"Проект #{s.GlpiProjectId} недоступен: {resp.StatusCode} {body}");

            using var doc = JsonDocument.Parse(body);
            var name = doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() : "?";
            return (true, $"Соединение есть. Проект #{s.GlpiProjectId}: «{name}».");
        }
        catch (Exception ex) { return (false, "Ошибка: " + ex.Message); }
        finally { if (token is not null) await KillSessionAsync(http, s, token); }
    }

    // --- Кэш задач-категорий --------------------------------------------------------------------

    private Dictionary<string, int> ParseCategoryCache(ServerSettings s)
    {
        if (string.IsNullOrWhiteSpace(s.GlpiCategoryTasksJson)) return new();
        try { return JsonSerializer.Deserialize<Dictionary<string, int>>(s.GlpiCategoryTasksJson!) ?? new(); }
        catch { return new(); }
    }

    /// <summary>Находит ID родительской задачи-категории в кэше или заводит её в GLPI (и сохраняет в кэш).</summary>
    private async Task<int> EnsureCategoryTaskAsync(HttpClient http, ServerSettings s, string sessionToken, AlertKind kind)
    {
        var cache = ParseCategoryCache(s);
        if (cache.TryGetValue(kind.ToString(), out var id) && id > 0) return id;

        var name = CategoryNames.TryGetValue(kind, out var n) ? n : kind.ToString();
        var newId = await CreateAsync(http, s, sessionToken, "ProjectTask", new
        {
            projects_id = s.GlpiProjectId,
            name,
            content = $"Группирующая задача: сюда попадают подзадачи-инциденты «{name}», по всем клиентам и серверам."
        });

        cache[kind.ToString()] = newId;
        s.GlpiCategoryTasksJson = JsonSerializer.Serialize(cache);
        await _db.SaveChangesAsync();
        return newId;
    }

    // --- Открытие / обновление / закрытие инцидента ---------------------------------------------

    /// <summary>
    /// Проблема появилась (или всё ещё активна) — создаёт подзадачу, если её ещё нет,
    /// иначе ничего не делает (повторный вызов по той же паре сервер+тип безопасен).
    /// Вызывать только на ПЕРЕХОДЕ в проблемное состояние — не на каждый отчёт.
    /// </summary>
    public async Task OpenAsync(ServerSettings s, MonitoredServer server, AlertKind kind, string plainBody)
    {
        if (!Configured(s)) return;

        var existing = await _db.GlpiIncidents
            .FirstOrDefaultAsync(g => g.ServerId == server.Id && g.Kind == kind.ToString() && g.ClosedAt == null);
        if (existing is not null) return; // уже открыта — повторно не создаём

        using var http = CreateClient();
        string? token = null;
        try
        {
            token = await InitSessionAsync(http, s);
            if (token is null) { Log(server, "не удалось открыть сессию GLPI"); return; }

            var categoryId = await EnsureCategoryTaskAsync(http, s, token, kind);
            var categoryWord = CategoryNames.TryGetValue(kind, out var cn) ? cn : kind.ToString();
            var taskId = await CreateAsync(http, s, token, "ProjectTask", new
            {
                projects_id = s.GlpiProjectId,
                projecttasks_id = categoryId,
                name = $"{server.Name} — {categoryWord}",
                content = plainBody
            });

            _db.GlpiIncidents.Add(new GlpiIncident { ServerId = server.Id, Kind = kind.ToString(), GlpiTaskId = taskId });
            await _db.SaveChangesAsync();
        }
        catch (Exception ex) { Log(server, "не удалось создать задачу в GLPI: " + ex.Message); }
        finally { if (token is not null) await KillSessionAsync(http, s, token); }
    }

    /// <summary>Проблема ушла — дописывает в задачу «восстановилось» и закрывает локальную запись.</summary>
    public async Task CloseAsync(ServerSettings s, MonitoredServer server, AlertKind kind, string note)
    {
        if (!Configured(s)) return;

        var existing = await _db.GlpiIncidents
            .FirstOrDefaultAsync(g => g.ServerId == server.Id && g.Kind == kind.ToString() && g.ClosedAt == null);
        if (existing is null) return; // нечего закрывать (не было открыто через GLPI)

        using var http = CreateClient();
        string? token = null;
        try
        {
            token = await InitSessionAsync(http, s);
            if (token is null) { Log(server, "не удалось открыть сессию GLPI (закрытие)"); return; }

            await UpdateAsync(http, s, token, "ProjectTask", existing.GlpiTaskId, new
            {
                percent_done = 100,
                content = note
            });

            existing.ClosedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync();
        }
        catch (Exception ex) { Log(server, "не удалось закрыть задачу в GLPI: " + ex.Message); }
        finally { if (token is not null) await KillSessionAsync(http, s, token); }
    }

    /// <summary>
    /// Разовая синхронизация: заводит задачи в GLPI по всем УЖЕ активным сейчас проблемам —
    /// то есть по тем, чей переход в проблемное состояние случился ДО включения интеграции
    /// (на будущие переходы заводить задачи не нужно, это делают AvailabilityMonitor/ReportIngestService
    /// сами). Безопасно вызывать повторно: OpenAsync не создаёт вторую задачу, если уже есть открытая.
    /// Вызывается кнопкой «Синхронизировать сейчас» в настройках и один раз при первом включении.
    /// </summary>
    public async Task<string> SyncCurrentProblemsAsync(ServerSettings s)
    {
        if (!Configured(s)) return "GLPI не настроен.";

        var servers = await _db.Servers.Include(x => x.Client).ToListAsync();
        var opened = 0;

        foreach (var srv in servers)
        {
            var name = srv.Name;

            if (srv.AlertOfflineActive)
            {
                await OpenAsync(s, srv, AlertKind.Offline,
                    $"🔴 {name} — пропала связь с сервером: агент не выходит на связь (последний отчёт: {UiHelpers.Ago(srv.LastSeenAt)}).");
                opened++;
            }
            if (srv.AlertPingActive)
            {
                await OpenAsync(s, srv, AlertKind.Ping, $"🌐🔴 {name} — внешний адрес не отвечает на ping (активная тревога).");
                opened++;
            }
            if (!string.IsNullOrWhiteSpace(srv.AlertDisksActiveJson) && srv.AlertDisksActiveJson != "[]")
            {
                List<string>? disks = null;
                try { disks = JsonSerializer.Deserialize<List<string>>(srv.AlertDisksActiveJson!); } catch { }
                var list = disks is { Count: > 0 } ? string.Join(", ", disks) : "см. карточку сервера";
                await OpenAsync(s, srv, AlertKind.DiskLow, $"💽🔴 {name} — мало места на диске: {list}.");
                opened++;
            }
            if (srv.AlertDiskHealthActive)
            {
                await OpenAsync(s, srv, AlertKind.DiskHealth, $"🩺🔴 {name} — проблема со здоровьем накопителя (SMART/температура), см. карточку сервера.");
                opened++;
            }
            if (srv.AlertCpuOverheatActive)
            {
                var t = srv.LastCpuTemperatureC is { } c ? $"{c:0.#} °C" : "см. карточку сервера";
                await OpenAsync(s, srv, AlertKind.CpuOverheat, $"🔥🔴 {name} — перегрев процессора: {t}.");
                opened++;
            }
            if (srv.BackupShrinkActive)
            {
                var folder = string.IsNullOrWhiteSpace(srv.BackupShrinkFolder) ? "" : $" (папка «{srv.BackupShrinkFolder}»)";
                await OpenAsync(s, srv, AlertKind.BackupShrink,
                    $"⚠️ {name} — новый файл бэкапа резко меньше предыдущего{folder}: "
                    + $"{UiHelpers.Bytes(srv.BackupShrinkFromBytes)} → {UiHelpers.Bytes(srv.BackupShrinkToBytes)}.");
                opened++;
            }
            if (!srv.IsOffline(s.OfflineThresholdSeconds) && srv.LastOutcome >= JobOutcome.Warning)
            {
                var problems = new List<string>();
                if (!string.IsNullOrWhiteSpace(srv.LastReportJson))
                {
                    try
                    {
                        var rep = JsonSerializer.Deserialize<AgentReportDto>(srv.LastReportJson!);
                        if (rep is not null)
                            problems = rep.Folders.Where(f => f.Outcome >= JobOutcome.Warning)
                                .Select(f => $"• {f.Name}: {f.Message}").ToList();
                    }
                    catch { }
                }
                var body = $"{srv.Client?.Name} — отчёт по архивации\n\n❌ {name} — ошибка архивации\n"
                    + (problems.Count > 0 ? string.Join("\n", problems) : "см. карточку сервера");
                await OpenAsync(s, srv, AlertKind.Backup, body);
                opened++;
            }
        }

        return $"Синхронизация выполнена: проверено серверов {servers.Count}, активных проблем заведено/подтверждено {opened}.";
    }

    private void Log(MonitoredServer server, string message)
    {
        _log.LogWarning("GLPI ({Server}): {Message}", server.Name, message);
        _logs.Warn(LogArea.Notify, "GLPI: " + message, server.Name);
    }
}
