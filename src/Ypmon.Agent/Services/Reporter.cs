using System.Net.Http.Json;
using System.Reflection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Ypmon.Shared;

namespace Ypmon.Agent.Services;

/// <summary>
/// Фоновая служба: формирует отчёт (папки + ошибки журнала) и ОТПРАВЛЯЕТ его на сервер
/// (только исходящее соединение). Команды от сервера не исполняются.
/// </summary>
public class Reporter : BackgroundService
{
    private readonly ConfigStore _store;
    private readonly FolderMonitorService _folders;
    private readonly EventLogReaderService _events;
    private readonly RemoteCommandHandler _commands;   // [YPMON-REMOTE-CMD] вырезать после релиза
    private readonly ILogger<Reporter> _log;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public static readonly string Version =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";

    public Reporter(ConfigStore store, FolderMonitorService folders, EventLogReaderService events,
        RemoteCommandHandler commands, ILogger<Reporter> log)
    {
        _store = store; _folders = folders; _events = events; _commands = commands; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var full = RunLoop(stoppingToken, heartbeat: false, cfg => Math.Max(30, cfg.ReportIntervalSeconds));
        var heartbeat = RunLoop(stoppingToken, heartbeat: true, cfg => Math.Max(15, cfg.HeartbeatIntervalSeconds));
        await Task.WhenAll(full, heartbeat);
    }

    private async Task RunLoop(CancellationToken ct, bool heartbeat, Func<AgentConfig, int> intervalSeconds)
    {
        if (heartbeat) { try { await Task.Delay(TimeSpan.FromSeconds(5), ct); } catch { } }

        while (!ct.IsCancellationRequested)
        {
            var cfg = _store.Load();
            try { await ReportOnceAsync(cfg, heartbeat); }
            catch (Exception ex)
            {
                _log.LogError(ex, "Ошибка отправки отчёта ({Kind})", heartbeat ? "связь" : "состояние");
                if (!heartbeat) PersistSnapshot(false, ex.Message, ex.Message, null);
            }
            try { await Task.Delay(TimeSpan.FromSeconds(intervalSeconds(cfg)), ct); } catch { }
        }
    }

    public AgentReportDto BuildReport(AgentConfig cfg, bool heartbeat)
    {
        return new AgentReportDto
        {
            MachineName = Environment.MachineName,
            AgentVersion = Version,
            ReportedAt = DateTimeOffset.UtcNow,
            IsHeartbeat = heartbeat,
            Folders = heartbeat ? new() : _folders.Build(cfg),
            // Диски передаём и в heartbeat: сбор дешёвый, а серверные пороги
            // заполненности работают со свежими данными (раз в минуту, а не раз в 6 часов).
            Disks = CollectDisks()
        };
    }

    /// <summary>Локальные (несъёмные) диски: буква, метка, объём, свободно.</summary>
    public static List<DiskStatusDto> CollectDisks()
    {
        var list = new List<DiskStatusDto>();
        try
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                    list.Add(new DiskStatusDto
                    {
                        Name = d.Name.TrimEnd('\\', '/'),
                        Label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? null : d.VolumeLabel,
                        TotalBytes = d.TotalSize,
                        FreeBytes = d.TotalFreeSpace
                    });
                }
                catch { /* отдельный диск может быть недоступен — пропускаем */ }
            }
        }
        catch { }
        return list;
    }

    private async Task ReportOnceAsync(AgentConfig cfg, bool heartbeat)
    {
        var report = BuildReport(cfg, heartbeat);

        if (string.IsNullOrWhiteSpace(cfg.ServerUrl) || string.IsNullOrWhiteSpace(cfg.ApiKey))
        {
            if (!heartbeat)
                PersistSnapshot(false, "Не задан адрес сервера или API-ключ", null, report);
            return;
        }

        // Ошибки журнала собираем только для полного отчёта, перед отправкой.
        if (!heartbeat && OperatingSystem.IsWindows())
        {
            try { report.EventLogErrors = _events.Collect(cfg); }
            catch (Exception ex) { _log.LogWarning(ex, "Не удалось прочитать журнал событий"); }
        }

        var (ack, statusCode) = await PostAsync(cfg, report);

        if (!heartbeat)
            PersistSnapshot(
                accepted: ack?.Accepted ?? false,
                message: ack?.Message ?? $"HTTP {statusCode}",
                error: null,
                report: report,
                clientName: ack?.ClientName,
                serverName: ack?.ServerName);

        // [YPMON-REMOTE-CMD] Флаги-команды из ответа сервера (запрос отчёта, принудительное
        // обновление) — обрабатываются отдельным классом, вырезать после релиза.
        if (heartbeat && ack is not null)
            await _commands.HandleAsync(cfg, ack, () => ReportOnceAsync(cfg, heartbeat: false));
    }

    private async Task<(ReportAckDto? ack, int status)> PostAsync(AgentConfig cfg, AgentReportDto report)
    {
        var url = cfg.ServerUrl.TrimEnd('/') + "/api/report";
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(report) };
        req.Headers.Add("X-Api-Key", cfg.ApiKey);
        var resp = await Http.SendAsync(req);
        try { return (await resp.Content.ReadFromJsonAsync<ReportAckDto>(), (int)resp.StatusCode); }
        catch { return (null, (int)resp.StatusCode); }
    }

    private void PersistSnapshot(bool accepted, string? message, string? error,
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
}
