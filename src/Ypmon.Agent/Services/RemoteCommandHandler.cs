using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Ypmon.Shared;

namespace Ypmon.Agent.Services;

/// <summary>
/// ==========================================================================
/// [YPMON-REMOTE-CMD] Обработка команд сервера — ВЕСЬ ФАЙЛ ВЫРЕЗАТЬ ПОСЛЕ РЕЛИЗА.
///
/// По изначальному замыслу агент строго исходящий и команд не получает.
/// Здесь собрано ВСЁ временное исключение на стороне агента: флаги, которые
/// агент сам вычитывает из ответа сервера на heartbeat (порты не слушает):
///   • ReportRequested — прислать полный отчёт сейчас;
///   • UpdateRequested — немедленно проверить и установить обновление.
///
/// Как вырезать: удалить этот файл, вызов в Reporter и все места с пометкой
/// [YPMON-REMOTE-CMD] (поиск по коду: YPMON-REMOTE-CMD).
/// ==========================================================================
/// </summary>
public class RemoteCommandHandler
{
    private readonly ILogger<RemoteCommandHandler> _log;
    private readonly IHostApplicationLifetime _life;
    private int _updating;   // защита от параллельного запуска обновления

    public RemoteCommandHandler(ILogger<RemoteCommandHandler> log, IHostApplicationLifetime life)
    {
        _log = log; _life = life;
    }

    /// <summary>[YPMON-REMOTE-CMD] Выполняет команды из ответа сервера на heartbeat.</summary>
    public async Task HandleAsync(AgentConfig cfg, ReportAckDto ack, Func<Task> sendFullReportAsync)
    {
        if (ack.ReportRequested)
        {
            _log.LogInformation("Сервер запросил отчёт — отправляем внеплановый полный отчёт.");
            await sendFullReportAsync();
        }

        if (ack.UpdateRequested)
            await ForceUpdateAsync(cfg);
    }

    /// <summary>[YPMON-REMOTE-CMD] Принудительное обновление: проверить версию на сервере и установить.</summary>
    private async Task ForceUpdateAsync(AgentConfig cfg)
    {
        if (Interlocked.Exchange(ref _updating, 1) == 1) return;
        try
        {
            var info = await UpdateInstaller.CheckAsync(cfg);
            if (!info.Available || !UpdateInstaller.IsNewer(info.Version, Reporter.Version))
            {
                _log.LogInformation("Принудительное обновление: новой версии нет (на сервере {Srv}, текущая {Cur}).",
                    info.Version ?? "—", Reporter.Version);
                return;
            }
            if (!Program.IsServiceMode)
            {
                _log.LogInformation("Принудительное обновление до {Ver} доступно, но установка выполняется только " +
                                    "в режиме службы (окно настроек не прерываем).", info.Version);
                return;
            }

            var installer = await UpdateInstaller.DownloadAsync(cfg);
            _log.LogInformation("Принудительное обновление {Ver}: установщик загружен, тихая установка и перезапуск службы.", info.Version);
            UpdateInstaller.RunInstaller(installer, silent: true);
            // Останавливаемся, чтобы установщик заменил файлы; службу он запустит заново сам.
            _life.StopApplication();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Принудительное обновление не удалось");
        }
        finally { Interlocked.Exchange(ref _updating, 0); }
    }
}
