using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ypmon.Agent.Services;

/// <summary>
/// Фоновая служба: проверяет обновление агента на сервере и устанавливает его.
/// Расписание настраивается (каждые N минут — по умолчанию час — или раз в сутки в заданное время).
/// </summary>
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

        // Просыпаемся часто и сами решаем, наступил ли момент проверки по настроенному расписанию.
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickAsync(); }
            catch (Exception ex) { _log.LogWarning(ex, "Ошибка проверки обновлений"); }
            try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); } catch { }
        }
    }

    /// <summary>Наступил ли момент очередной проверки обновлений по настроенному расписанию.</summary>
    private static bool IsDue(AgentConfig cfg, DateTimeOffset now)
    {
        var last = cfg.LastUpdateCheck;
        if (string.Equals(cfg.UpdateCheckMode, "DailyTime", StringComparison.OrdinalIgnoreCase))
        {
            // Раз в сутки в заданное время (по местному времени машины).
            var parts = (cfg.UpdateCheckAtTime ?? "03:00").Split(':');
            if (parts.Length != 2 || !int.TryParse(parts[0], out var h) || !int.TryParse(parts[1], out var m))
                { h = 3; m = 0; }
            var localNow = now.ToLocalTime();
            var todayTarget = new DateTimeOffset(localNow.Year, localNow.Month, localNow.Day, h, m, 0, localNow.Offset);
            // Пора, если время наступило и с этого целевого момента ещё не проверяли.
            return localNow >= todayTarget && (last is null || last.Value.ToLocalTime() < todayTarget);
        }

        // Иначе — интервал в минутах (по умолчанию 60).
        var minutes = Math.Clamp(cfg.UpdateCheckIntervalMinutes <= 0 ? 60 : cfg.UpdateCheckIntervalMinutes, 5, 7 * 24 * 60);
        return last is null || (now - last.Value).TotalMinutes >= minutes;
    }

    private async Task TickAsync()
    {
        var cfg = _store.Load();
        if (!cfg.AutoUpdate) return;                 // автообновление выключено — молча
        if (!IsDue(cfg, DateTimeOffset.UtcNow)) return;

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
