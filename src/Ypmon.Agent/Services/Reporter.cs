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
    private readonly SpoolStore _spool;
    private readonly FolderMonitorService _folders;
    private readonly EventLogReaderService _events;
    private readonly RemoteCommandHandler _commands;   // [YPMON-REMOTE-CMD] вырезать после релиза
    private readonly ILogger<Reporter> _log;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public static readonly string Version =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";

    public Reporter(ConfigStore store, SpoolStore spool, FolderMonitorService folders, EventLogReaderService events,
        RemoteCommandHandler commands, ILogger<Reporter> log)
    {
        _store = store; _spool = spool; _folders = folders; _events = events; _commands = commands; _log = log;
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
            Disks = CollectDisks(),
            // Здоровье накопителей (SMART) — только в полном отчёте: сбор через WMI дороже.
            PhysicalDisks = heartbeat ? new() : CollectPhysicalDisks(),
            HeartbeatIntervalSeconds = Math.Max(15, cfg.HeartbeatIntervalSeconds),
            ReportIntervalSeconds = Math.Max(60, cfg.ReportIntervalSeconds)
        };
    }

    /// <summary>Здоровье физических накопителей (SMART) — только на Windows, best-effort.</summary>
    private static List<PhysicalDiskDto> CollectPhysicalDisks()
        => OperatingSystem.IsWindows() ? PhysicalDiskCollector.Collect() : new();

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

        if (heartbeat)
        {
            // Лёгкий отчёт о связи. При успехе — досылаем накопленную очередь полных отчётов.
            var (hack, hstatus) = await PostAsync(cfg, report);
            if (hstatus is >= 200 and < 300)
            {
                var flushed = await _spool.FlushAsync(r => TrySendAsync(cfg, r));
                if (flushed > 0) _log.LogInformation("Досланы отложенные отчёты: {N}", flushed);
            }
            // [YPMON-REMOTE-CMD] Флаги-команды из ответа сервера — вырезать после релиза.
            if (hack is not null)
                await _commands.HandleAsync(cfg, hack, () => ReportOnceAsync(cfg, heartbeat: false));
            return;
        }

        // Полный отчёт. Ошибки журнала собираем перед отправкой.
        if (OperatingSystem.IsWindows())
        {
            try { report.EventLogErrors = _events.Collect(cfg); }
            catch (Exception ex) { _log.LogWarning(ex, "Не удалось прочитать журнал событий"); }
        }

        ReportAckDto? ack = null;
        var sendOk = false;
        try
        {
            var (a, statusCode) = await PostAsync(cfg, report);
            ack = a;
            sendOk = statusCode is >= 200 and < 300;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Полный отчёт не отправлен — откладываю в очередь");
        }

        PersistSnapshot(
            accepted: ack?.Accepted ?? false,
            message: ack?.Message ?? (sendOk ? "отправлено" : "сервер недоступен — отчёт отложен в очередь"),
            error: sendOk ? null : "сервер недоступен",
            report: report,
            clientName: ack?.ClientName,
            serverName: ack?.ServerName);

        if (sendOk)
        {
            // На случай, если очередь копилась, а heartbeat её ещё не разобрал.
            await _spool.FlushAsync(r => TrySendAsync(cfg, r));
        }
        else
        {
            _spool.Enqueue(report);   // сервер недоступен — сохраняем и дошлём позже
        }
    }

    /// <summary>Отправить отчёт, вернуть true только при HTTP 2xx (для досыла очереди).</summary>
    private async Task<bool> TrySendAsync(AgentConfig cfg, AgentReportDto report)
    {
        try { var (_, status) = await PostAsync(cfg, report); return status is >= 200 and < 300; }
        catch { return false; }
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
