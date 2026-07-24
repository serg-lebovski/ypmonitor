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
    private readonly RemoteCommandHandler _commands;
    private readonly ILogger<Reporter> _log;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public static readonly string Version =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";

    public Reporter(ConfigStore store, SpoolStore spool, FolderMonitorService folders, EventLogReaderService events,
        RemoteCommandHandler commands, ILogger<Reporter> log)
    {
        _store = store; _spool = spool; _folders = folders; _events = events; _commands = commands; _log = log;
    }

    /// <summary>Последняя измеренная температура процессора (шлётся в отчётах, показывается в окне агента).</summary>
    public static double? LastCpuTemperatureC { get; private set; }

    /// <summary>Идёт ли сейчас перегрев — чтобы отправить срочный отчёт один раз, а не в каждом цикле.</summary>
    private bool _overheatActive;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var full = RunLoop(stoppingToken, heartbeat: false, cfg => Math.Max(30, cfg.ReportIntervalSeconds));
        var heartbeat = RunLoop(stoppingToken, heartbeat: true, cfg => Math.Max(15, cfg.HeartbeatIntervalSeconds));
        var temp = WatchCpuTemperatureAsync(stoppingToken);
        await Task.WhenAll(full, heartbeat, temp);
    }

    /// <summary>
    /// Следит за температурой процессора. При выходе за порог отправляет полный отчёт немедленно,
    /// не дожидаясь расписания, — сервер по нему сразу поднимет тревогу.
    /// </summary>
    private async Task WatchCpuTemperatureAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var cfg = _store.Load();
            var period = Math.Max(15, cfg.CpuTempCheckSeconds);
            try
            {
                var t = ReadCpuTemperature();
                LastCpuTemperatureC = t;

                if (t is { } temp && cfg.CpuTempTriggerC > 0)
                {
                    if (temp >= cfg.CpuTempTriggerC && !_overheatActive)
                    {
                        _overheatActive = true;
                        _log.LogWarning("Перегрев процессора: {Temp} °C (порог {Limit} °C) — отправляю внеплановый отчёт",
                            temp, cfg.CpuTempTriggerC);
                        await ReportOnceAsync(cfg, heartbeat: false, overheatTrigger: true);
                    }
                    // Гистерезис 5 °C: около порога температура скачет, иначе агент задёргал бы сервер.
                    else if (temp <= cfg.CpuTempTriggerC - 5 && _overheatActive)
                    {
                        _overheatActive = false;
                        _log.LogInformation("Температура процессора вернулась в норму: {Temp} °C", temp);
                        await ReportOnceAsync(cfg, heartbeat: false, overheatTrigger: true);
                    }
                }
            }
            catch (Exception ex) { _log.LogWarning(ex, "Ошибка проверки температуры процессора"); }

            try { await Task.Delay(TimeSpan.FromSeconds(period), ct); } catch { }
        }
    }

    /// <summary>Температура процессора — только на Windows, best-effort (может быть недоступна).</summary>
    private static double? ReadCpuTemperature()
        => OperatingSystem.IsWindows() ? CpuTemperatureCollector.Read() : null;

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

    public AgentReportDto BuildReport(AgentConfig cfg, bool heartbeat, bool overheatTrigger = false)
    {
        // Температуру берём из последнего замера наблюдателя, а не читаем WMI заново:
        // запрос не самый дешёвый, а свежесть в пределах минуты нас устраивает.
        return new AgentReportDto
        {
            CpuTemperatureC = LastCpuTemperatureC,
            CpuOverheatTrigger = overheatTrigger,
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
            ReportIntervalSeconds = Math.Max(60, cfg.ReportIntervalSeconds),
            // Номер последней выполненной команды — по нему сервер снимает её с очереди.
            LastCommandId = RemoteCommandHandler.LastExecutedId,
            // Аптайм — дёшево, шлём всегда; история перезагрузок — только в полном отчёте.
            LastBootAt = CollectLastBoot(),
            Reboots = heartbeat ? new() : CollectReboots()
        };
    }

    /// <summary>Здоровье физических накопителей (SMART) — только на Windows, best-effort.</summary>
    private static List<PhysicalDiskDto> CollectPhysicalDisks()
        => OperatingSystem.IsWindows() ? PhysicalDiskCollector.Collect() : new();

    private static DateTimeOffset? CollectLastBoot()
        => OperatingSystem.IsWindows() ? RebootHistoryCollector.LastBootAt() : null;

    private static List<RebootEventDto> CollectReboots()
        => OperatingSystem.IsWindows() ? RebootHistoryCollector.Collect() : new();

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

    private async Task ReportOnceAsync(AgentConfig cfg, bool heartbeat, bool overheatTrigger = false)
    {
        var report = BuildReport(cfg, heartbeat, overheatTrigger);

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
            // Команда из ответа сервера (закрытый список, исполняется в RemoteCommandHandler).
            if (hack is not null)
            {
                SyncFolderTypes(cfg, hack);
                await _commands.HandleAsync(cfg, hack, () => ReportOnceAsync(cfg, heartbeat: false));
            }
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

        if (ack is not null) SyncFolderTypes(cfg, ack);

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

    /// <summary>
    /// Синхронизация типа задания «сервер → агент»: агент сверяет тип каждой папки с тем, что
    /// пришло в ответе сервера, и если отличается — переписывает у себя и сохраняет config.
    /// Это НЕ команда: меняется только текстовая метка типа, ничего не исполняется.
    /// Сервер присылает лишь не-Full типы; отсутствие папки в словаре означает Full.
    /// </summary>
    private void SyncFolderTypes(AgentConfig cfg, ReportAckDto ack)
    {
        if (cfg.Folders.Count == 0) return;
        var changed = false;
        foreach (var f in cfg.Folders)
        {
            var serverType = ack.FolderBackupTypes.TryGetValue(f.Name, out var t)
                && t is "Full" or "Incremental" or "Differential" ? t : "Full";
            var local = string.IsNullOrWhiteSpace(f.BackupType) ? "Full" : f.BackupType;
            if (local != serverType)
            {
                _log.LogInformation("Тип задания «{Folder}» изменён на сервере: {Old} → {New}", f.Name, local, serverType);
                f.BackupType = serverType;
                changed = true;
            }
        }
        if (changed) _store.Save(cfg);
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
