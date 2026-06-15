using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ypmon.Agent.Services;

/// <summary>Фоновая служба: раз в день проверяет обновление агента на сервере и устанавливает его.</summary>
public class UpdateService : BackgroundService
{
    private readonly ConfigStore _store;
    private readonly ILogger<UpdateService> _log;
    private readonly IHostApplicationLifetime _life;

    public UpdateService(ConfigStore store, ILogger<UpdateService> log, IHostApplicationLifetime life)
    {
        _store = store; _log = log; _life = life;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); } catch { }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickAsync(); }
            catch (Exception ex) { _log.LogWarning(ex, "Ошибка проверки обновлений"); }
            try { await Task.Delay(TimeSpan.FromHours(6), stoppingToken); } catch { }
        }
    }

    private async Task TickAsync()
    {
        var cfg = _store.Load();
        if (!cfg.AutoUpdate) return;
        if (cfg.LastUpdateCheck is not null && (DateTimeOffset.UtcNow - cfg.LastUpdateCheck.Value).TotalHours < 24)
            return;

        var info = await UpdateInstaller.CheckAsync(cfg);

        cfg.LastUpdateCheck = DateTimeOffset.UtcNow;
        _store.Save(cfg);

        if (!info.Available || !UpdateInstaller.IsNewer(info.Version, Reporter.Version)) return;

        _log.LogInformation("Доступна новая версия агента: {Ver} (текущая {Cur})", info.Version, Reporter.Version);

        // Автоматическую замену выполняем только в режиме службы, чтобы не прерывать работу окна настроек.
        if (!Program.IsServiceMode)
        {
            _log.LogInformation("Обновление будет установлено службой YpmonAgent.");
            return;
        }

        var update = await UpdateInstaller.DownloadAsync(cfg);
        _log.LogInformation("Обновление загружено, выполняется установка и перезапуск службы.");
        UpdateInstaller.ApplyAndExit(cfg.ServiceName, isService: true, update);
        _life.StopApplication();
    }
}
