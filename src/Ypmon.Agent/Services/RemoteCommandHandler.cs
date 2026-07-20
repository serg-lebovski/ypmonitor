using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Ypmon.Shared;

namespace Ypmon.Agent.Services;

/// <summary>
/// Выполнение команд сервера. Список закрытый и живёт ЗДЕСЬ, в агенте:
/// с сервера приходит только номер команды. Никаких путей, аргументов и строк
/// для исполнения агент от сервера не принимает — и принимать не должен,
/// иначе компрометация сервера означала бы выполнение произвольного кода
/// на машинах всех клиентов.
///
/// Агент не слушает порты: команду он видит в ответе на собственный heartbeat.
///
/// Защита от повторов: номер последней выполненной команды пишется в файл рядом
/// с exe. Это важно именно для перезагрузки — иначе после рестарта агент увидел бы
/// ту же команду снова и ушёл в цикл перезагрузок.
/// </summary>
public class RemoteCommandHandler
{
    private readonly ILogger<RemoteCommandHandler> _log;
    private readonly IHostApplicationLifetime _life;
    private int _busy;   // защита от параллельного исполнения

    private static readonly string StatePath = Path.Combine(AppContext.BaseDirectory, "lastcmd.txt");
    private static readonly object StateLock = new();

    /// <summary>Отсрочка перезагрузки: агент успевает подтвердить приём команды серверу.</summary>
    private const int RebootDelaySeconds = 60;

    public RemoteCommandHandler(ILogger<RemoteCommandHandler> log, IHostApplicationLifetime life)
    {
        _log = log; _life = life;
    }

    /// <summary>Номер последней выполненной команды (уходит серверу в каждом отчёте).</summary>
    public static long LastExecutedId
    {
        get
        {
            lock (StateLock)
            {
                try
                {
                    return File.Exists(StatePath) && long.TryParse(File.ReadAllText(StatePath).Trim(), out var v) ? v : 0;
                }
                catch { return 0; }
            }
        }
    }

    private static void SaveExecuted(long id)
    {
        lock (StateLock)
        {
            try { File.WriteAllText(StatePath, id.ToString()); }
            catch { /* не смогли записать — хуже, но команда всё равно одноразовая на сервере */ }
        }
    }

    /// <summary>Выполняет команду из ответа сервера на heartbeat.</summary>
    public async Task HandleAsync(AgentConfig cfg, ReportAckDto ack, Func<Task> sendFullReportAsync)
    {
        // Новый механизм (сервер 2.7+): команда с номером и сроком годности.
        if (ack.CommandId != 0 && ack.Command != AgentCommand.None)
        {
            await ExecuteAsync(cfg, ack, sendFullReportAsync);
            return;
        }

        // Старый сервер: только флаги, без номеров. Перезагрузки среди них нет.
        if (ack.ReportRequested)
        {
            _log.LogInformation("Сервер запросил отчёт — отправляем внеплановый полный отчёт.");
            await sendFullReportAsync();
        }
        if (ack.UpdateRequested)
            await ForceUpdateAsync(cfg);
    }

    private async Task ExecuteAsync(AgentConfig cfg, ReportAckDto ack, Func<Task> sendFullReportAsync)
    {
        if (ack.CommandId == LastExecutedId)
            return;   // уже выполняли — молча игнорируем (защита от повтора)

        if (ack.CommandIssuedAt is { } issued &&
            DateTimeOffset.UtcNow - issued > TimeSpan.FromMinutes(ReportAckDto.CommandTtlMinutes))
        {
            _log.LogWarning("Команда {Cmd} №{Id} просрочена (выдана {At}) — не выполняется.",
                ack.Command, ack.CommandId, issued);
            SaveExecuted(ack.CommandId);   // чтобы не разбирать её снова при каждом heartbeat
            return;
        }

        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            _log.LogInformation("Получена команда сервера: {Cmd} (№{Id})", ack.Command, ack.CommandId);

            // Номер сохраняем ДО выполнения: перезагрузка не даст сделать это после.
            SaveExecuted(ack.CommandId);

            switch (ack.Command)
            {
                case AgentCommand.RequestReport:
                    await sendFullReportAsync();
                    break;

                case AgentCommand.UpdateAgent:
                    await ForceUpdateAsync(cfg);
                    break;

                case AgentCommand.RebootMachine:
                    await RebootAsync(sendFullReportAsync);
                    break;

                default:
                    _log.LogWarning("Неизвестная команда {Cmd} — пропущена.", ack.Command);
                    break;
            }
        }
        catch (Exception ex) { _log.LogWarning(ex, "Ошибка выполнения команды сервера"); }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    /// <summary>Перезагрузка машины с отсрочкой. Только в режиме службы — из окна настроек не перезагружаем.</summary>
    private async Task RebootAsync(Func<Task> sendFullReportAsync)
    {
        if (!Program.IsServiceMode)
        {
            _log.LogInformation("Команда перезагрузки получена, но агент запущен как окно настроек — игнорируем.");
            return;
        }
        if (!OperatingSystem.IsWindows())
        {
            _log.LogWarning("Команда перезагрузки поддерживается только на Windows.");
            return;
        }

        // Сначала подтверждаем серверу приём — иначе после перезагрузки он не узнает,
        // что команда исполнена, и будет предлагать её снова до истечения срока.
        try { await sendFullReportAsync(); } catch { }

        _log.LogWarning("Перезагрузка машины по команде сервера через {Sec} с.", RebootDelaySeconds);
        try
        {
            Process.Start(new ProcessStartInfo("shutdown.exe",
                $"/r /t {RebootDelaySeconds} /c \"Перезагрузка по команде YPMonitor\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            });
        }
        catch (Exception ex) { _log.LogError(ex, "Не удалось запустить перезагрузку"); }
    }

    /// <summary>Принудительное обновление: проверить версию на сервере и установить.</summary>
    private async Task ForceUpdateAsync(AgentConfig cfg)
    {
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
    }
}
