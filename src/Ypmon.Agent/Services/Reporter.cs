using System.Net.Http.Json;
using System.Net.Sockets;
using System.Reflection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Ypmon.Shared;

namespace Ypmon.Agent.Services;

/// <summary>
/// Фоновая служба: формирует отчёт и ОТПРАВЛЯЕТ его на сервер YPMon (только исходящее соединение).
/// Ответ сервера используется лишь для отображения имени клиента/сервера — никаких команд не исполняется.
/// </summary>
public class Reporter : BackgroundService
{
    private readonly ConfigStore _store;
    private readonly BackupRunner _runner;
    private readonly EventLogReaderService _events;
    private readonly ILogger<Reporter> _log;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public static readonly string Version =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";

    public Reporter(ConfigStore store, BackupRunner runner, EventLogReaderService events, ILogger<Reporter> log)
    {
        _store = store; _runner = runner; _events = events; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Два независимых таймера: полный отчёт о состоянии ПК и лёгкий heartbeat доступности.
        var full = RunLoop(stoppingToken, heartbeat: false, cfg => Math.Max(15, cfg.ReportIntervalSeconds));
        var availability = RunLoop(stoppingToken, heartbeat: true, cfg => Math.Max(10, cfg.AvailabilityIntervalSeconds));
        await Task.WhenAll(full, availability);
    }

    private async Task RunLoop(CancellationToken ct, bool heartbeat, Func<AgentConfig, int> intervalSeconds)
    {
        // Heartbeat стартует с небольшим сдвигом, чтобы не совпадать с полным отчётом.
        if (heartbeat) { try { await Task.Delay(TimeSpan.FromSeconds(5), ct); } catch { } }

        while (!ct.IsCancellationRequested)
        {
            var cfg = _store.Load();
            try { await ReportOnceAsync(cfg, heartbeat); }
            catch (Exception ex)
            {
                _log.LogError(ex, "Ошибка отправки отчёта ({Kind})", heartbeat ? "доступность" : "состояние");
                if (!heartbeat) PersistSnapshot(cfg, false, ex.Message, ex.Message, null);
            }
            try { await Task.Delay(TimeSpan.FromSeconds(intervalSeconds(cfg)), ct); } catch { }
        }
    }

    public AgentReportDto BuildReport(AgentConfig cfg, bool heartbeat = false)
    {
        var (available, msg) = CheckAvailability(cfg);
        return new AgentReportDto
        {
            MachineName = Environment.MachineName,
            AgentVersion = Version,
            ReportedAt = DateTimeOffset.UtcNow,
            IsHeartbeat = heartbeat,
            ServerAvailable = available,
            AvailabilityMessage = msg,
            UptimeSeconds = SystemInfo.UptimeSeconds(),
            // В heartbeat не собираем задания/диски — это лёгкая проверка доступности.
            Jobs = heartbeat ? new() : _runner.BuildJobStatuses(cfg),
            Disks = heartbeat ? new() : SystemInfo.GetDisks()
        };
    }

    private async Task ReportOnceAsync(AgentConfig cfg, bool heartbeat)
    {
        var report = BuildReport(cfg, heartbeat);

        if (string.IsNullOrWhiteSpace(cfg.ServerUrl) || string.IsNullOrWhiteSpace(cfg.ApiKey))
        {
            if (!heartbeat)
                PersistSnapshot(cfg, false, "Не задан адрес сервера или API-ключ", null, report);
            return;
        }

        // Новые ошибки журнала событий Windows собираем только для полного отчёта и только перед отправкой
        // (чтобы отметка прочитанного не сдвигалась без реальной передачи данных).
        if (!heartbeat && OperatingSystem.IsWindows())
        {
            try { report.EventLogErrors = _events.Collect(cfg); }
            catch (Exception ex) { _log.LogWarning(ex, "Не удалось прочитать журнал событий"); }
        }

        var url = cfg.ServerUrl.TrimEnd('/') + "/api/report";
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(report) };
        req.Headers.Add("X-Api-Key", cfg.ApiKey);

        var resp = await Http.SendAsync(req);
        ReportAckDto? ack = null;
        try { ack = await resp.Content.ReadFromJsonAsync<ReportAckDto>(); } catch { }

        // Снапшот для локального UI обновляем по полному отчёту.
        if (!heartbeat)
            PersistSnapshot(cfg,
                accepted: ack?.Accepted ?? false,
                message: ack?.Message ?? $"HTTP {(int)resp.StatusCode}",
                error: null,
                report: report,
                clientName: ack?.ClientName,
                serverName: ack?.ServerName);
    }

    private void PersistSnapshot(AgentConfig cfg, bool accepted, string? message, string? error,
        AgentReportDto? report, string? clientName = null, string? serverName = null)
    {
        _store.SaveSnapshot(new AgentSnapshot
        {
            LastReportAt = DateTimeOffset.UtcNow,
            LastReportAccepted = accepted,
            LastReportMessage = message,
            ResolvedClientName = clientName,
            ResolvedServerName = serverName,
            LastError = error,
            LastReport = report
        });
    }

    /// <summary>Проверка доступности наблюдаемого сервера БД (TCP к postgres-хостам).</summary>
    private (bool, string?) CheckAvailability(AgentConfig cfg)
    {
        var hosts = cfg.PostgresJobs.Where(j => j.Enabled)
            .Select(j => (j.Host, j.Port)).Distinct().ToList();
        if (hosts.Count == 0) return (true, "Нет наблюдаемых БД (агент работает)");

        var unreachable = new List<string>();
        foreach (var (host, port) in hosts)
            if (!TryTcp(host, port)) unreachable.Add($"{host}:{port}");

        return unreachable.Count == 0
            ? (true, "Все БД доступны")
            : (false, "Недоступны: " + string.Join(", ", unreachable));
    }

    private static bool TryTcp(string host, int port)
    {
        try
        {
            using var client = new TcpClient();
            var task = client.ConnectAsync(host, port);
            return task.Wait(TimeSpan.FromSeconds(3)) && client.Connected;
        }
        catch { return false; }
    }
}
