using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ypmon.Agent.Services;

/// <summary>Фоновая служба: раз в день проверяет обновление агента на сервере и устанавливает его.</summary>
public class UpdateService : BackgroundService
{
    private readonly ConfigStore _store;
    private readonly ILogger<UpdateService> _log;

    public UpdateService(ConfigStore store, ILogger<UpdateService> log)
    {
        _store = store; _log = log;
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
        if (!cfg.AutoUpdate)
        {
            _log.LogInformation("Проверка обновлений пропущена: автообновление выключено в настройках агента.");
            return;
        }
        if (cfg.LastUpdateCheck is not null && (DateTimeOffset.UtcNow - cfg.LastUpdateCheck.Value).TotalHours < 24)
            return;   // троттлинг раз в сутки — молча, чтобы не засорять лог

        _log.LogInformation("Проверка обновлений на сервере {Server}. Текущая версия агента: {Cur}.",
            string.IsNullOrWhiteSpace(cfg.ServerUrl) ? "(адрес не задан)" : cfg.ServerUrl, Reporter.Version);

        UpdateInstaller.VersionInfo info;
        try
        {
            info = await UpdateInstaller.CheckAsync(cfg);
        }
        catch (Exception ex)
        {
            _log.LogWarning("Не удалось проверить обновление (обновление не выполнено): {Reason}", ex.Message);
            return;
        }

        cfg.LastUpdateCheck = DateTimeOffset.UtcNow;
        _store.Save(cfg);

        if (!info.Available || string.IsNullOrWhiteSpace(info.Version))
        {
            _log.LogInformation("Новой версии не видно: сервер не сообщил доступную версию " +
                "(не задан адрес/ключ, установщик не выложен на сервер или сервер недоступен). Обновление не выполнено.");
            return;
        }

        if (!UpdateInstaller.IsNewer(info.Version, Reporter.Version))
        {
            _log.LogInformation("Обновление не требуется: на сервере {Ver}, установлена {Cur} — уже актуально.",
                info.Version, Reporter.Version);
            return;
        }

        _log.LogInformation("Видит новую версию агента: {Ver} (текущая {Cur}).", info.Version, Reporter.Version);

        // Автоматическую замену выполняем только в режиме службы, чтобы не прерывать работу окна настроек.
        if (!Program.IsServiceMode)
        {
            _log.LogInformation("Сейчас не обновляюсь: агент запущен в режиме окна настроек. " +
                "Обновление установит служба YpmonAgent при своей проверке.");
            return;
        }

        try
        {
            var installer = await UpdateInstaller.DownloadAsync(cfg);
            _log.LogInformation("Установщик {Ver} загружен, запуск тихой установки. Установщик сам остановит " +
                "службу, заменит файлы и запустит её заново.", info.Version);
            UpdateInstaller.RunInstaller(installer, silent: true);
            // Себя НЕ останавливаем: остановкой службы управляет установщик (он ждёт фактической
            // остановки перед заменой файлов). Раньше агент останавливался сам и гонка с установщиком
            // приводила к тому, что служба оставалась остановленной на старой версии.
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Не удалось скачать/запустить установщик обновления {Ver} — обновление не выполнено.", info.Version);
        }
    }
}
