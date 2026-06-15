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
    private readonly ILogger<Reporter> _log;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public static readonly string Version =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";

    public Reporter(ConfigStore store, BackupRunner runner, ILogger<Reporter> log)
    {
        _store = store; _runner = runner; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var cfg = _store.Load();
            try { await ReportOnceAsync(cfg); }
            catch (Exception ex)
            {
                _log.LogError(ex, "Ошибка отправки отчёта");
                PersistSnapshot(cfg, accepted: false, message: ex.Message, error: ex.Message, report: null);
            }

            var interval = Math.Max(15, cfg.ReportIntervalSeconds);
            try { await Task.Delay(TimeSpan.FromSeconds(interval), stoppingToken); } catch { }
        }
    }

    public AgentReportDto BuildReport(AgentConfig cfg)
    {
        var (available, msg) = CheckAvailability(cfg);
        return new AgentReportDto
        {
            MachineName = Environment.MachineName,
            AgentVersion = Version,
            ReportedAt = DateTimeOffset.UtcNow,
            ServerAvailable = available,
            AvailabilityMessage = msg,
            UptimeSeconds = SystemInfo.UptimeSeconds(),
            Jobs = _runner.BuildJobStatuses(cfg),
            Disks = SystemInfo.GetDisks()
        };
    }

    private async Task ReportOnceAsync(AgentConfig cfg)
    {
        var report = BuildReport(cfg);

        if (string.IsNullOrWhiteSpace(cfg.ServerUrl) || string.IsNullOrWhiteSpace(cfg.ApiKey))
        {
            PersistSnapshot(cfg, accepted: false,
                message: "Не задан адрес сервера или API-ключ", error: null, report: report);
            return;
        }

        var url = cfg.ServerUrl.TrimEnd('/') + "/api/report";
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(report) };
        req.Headers.Add("X-Api-Key", cfg.ApiKey);

        var resp = await Http.SendAsync(req);
        ReportAckDto? ack = null;
        try { ack = await resp.Content.ReadFromJsonAsync<ReportAckDto>(); } catch { }

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
