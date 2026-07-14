using System.ComponentModel;
using System.Diagnostics;
using System.ServiceProcess;
using System.Text;
using System.Text.RegularExpressions;

namespace Ypmon.Agent.Services;

/// <summary>
/// Установка/удаление/управление службой Windows для агента.
/// Действия, требующие прав администратора, выполняются через sc.exe с запросом UAC.
/// </summary>
public static class ServiceManager
{
    public const string DisplayName = "YPMon Agent";

    /// <summary>Путь к текущему исполняемому файлу агента.</summary>
    public static string ExePath =>
        Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule!.FileName!;

    public static (bool exists, bool running, string status) Query(string name)
    {
        try
        {
            var sc = ServiceController.GetServices()
                .FirstOrDefault(s => string.Equals(s.ServiceName, name, StringComparison.OrdinalIgnoreCase));
            if (sc is null) return (false, false, "не установлена");
            var running = sc.Status == ServiceControllerStatus.Running;
            return (true, running, sc.Status.ToString());
        }
        catch (Exception ex)
        {
            return (false, false, "ошибка: " + ex.Message);
        }
    }

    /// <summary>
    /// Проверяет имя службы до установки. sc.exe не принимает пробелы и служебные символы в имени —
    /// именно из-за этого создание молча падает, а последующий «запуск» выдаёт 1060.
    /// </summary>
    public static string? ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Имя службы не заполнено.";
        if (name.Length > 80) return "Имя службы длиннее 80 символов.";
        if (name.Any(char.IsWhiteSpace)) return "Имя службы не должно содержать пробелов (например: YpmonAgent).";
        foreach (var c in "/\\:*?\"<>|")
            if (name.Contains(c)) return $"Имя службы не должно содержать символ «{c}».";
        return null;
    }

    /// <summary>
    /// Команда установки службы для ручного запуска в консоли (cmd от имени администратора).
    /// Пароль намеренно не подставляется — его нужно вписать вручную, чтобы не светить в интерфейсе.
    /// </summary>
    public static string BuildInstallCommand(string name, string? account)
    {
        var cmd = $"sc create \"{name}\" binPath= \"{ExePath}\" start= auto DisplayName= \"{DisplayName}\"";
        if (!string.IsNullOrWhiteSpace(account))
            cmd += $" obj= \"{account.Trim()}\" password= \"ПАРОЛЬ\"";
        return cmd + $" && sc start \"{name}\"";
    }

    /// <summary>
    /// Устанавливает службу с заданным именем. Если указан account — служба запускается под ним
    /// (DOMAIN\User, .\User), иначе под LocalSystem.
    /// </summary>
    public static string Install(string name, string? account, string? password)
    {
        var obj = string.IsNullOrWhiteSpace(account)
            ? ""
            : $"obj= \"{Bat(account.Trim())}\" password= \"{Bat(password ?? "")}\"";

        var script = new StringBuilder();
        script.AppendLine("@echo off");
        script.AppendLine("chcp 65001>nul");
        script.AppendLine($"sc stop \"{Bat(name)}\" >nul 2>&1");
        script.AppendLine("ping -n 2 127.0.0.1 >nul");
        script.AppendLine($"sc delete \"{Bat(name)}\" >nul 2>&1");
        script.AppendLine("ping -n 2 127.0.0.1 >nul");
        script.AppendLine("echo === Создание службы ===> \"%LOG%\"");
        script.AppendLine($"sc create \"{Bat(name)}\" binPath= \"{Bat(ExePath)}\" start= auto DisplayName= \"{DisplayName}\" {obj} >> \"%LOG%\" 2>&1");
        // Если создать службу не удалось — прекращаем. Иначе sc description/failure/start посыпались бы
        // ошибками 1060 («служба не существует»), маскируя настоящую причину.
        script.AppendLine("if errorlevel 1 (echo [ПРЕРВАНО] Служба не создана, остальные шаги пропущены.>> \"%LOG%\" & exit /b 1)");
        script.AppendLine($"sc description \"{Bat(name)}\" \"Агент мониторинга резервного копирования YPMon\" >> \"%LOG%\" 2>&1");
        script.AppendLine($"sc failure \"{Bat(name)}\" reset= 60 actions= restart/5000/restart/5000/restart/5000 >> \"%LOG%\" 2>&1");
        script.AppendLine("echo.>> \"%LOG%\"");
        script.AppendLine("echo === Запуск службы ===>> \"%LOG%\"");
        script.AppendLine($"sc start \"{Bat(name)}\" >> \"%LOG%\" 2>&1");
        return RunElevated(script.ToString());
    }

    public static string Uninstall(string name)
    {
        var script = new StringBuilder();
        script.AppendLine("@echo off");
        script.AppendLine("chcp 65001>nul");
        script.AppendLine($"sc stop \"{Bat(name)}\" > \"%LOG%\" 2>&1");
        script.AppendLine("ping -n 3 127.0.0.1 >nul");
        script.AppendLine($"sc delete \"{Bat(name)}\" >> \"%LOG%\" 2>&1");
        return RunElevated(script.ToString());
    }

    public static string Start(string name) => RunElevated($"@echo off\r\nchcp 65001>nul\r\nsc start \"{Bat(name)}\" > \"%LOG%\" 2>&1\r\n");
    public static string Stop(string name) => RunElevated($"@echo off\r\nchcp 65001>nul\r\nsc stop \"{Bat(name)}\" > \"%LOG%\" 2>&1\r\n");

    /// <summary>
    /// Человеческое объяснение кодов ошибок sc.exe. Возвращает пустую строку, если знакомых кодов нет.
    /// </summary>
    public static string Explain(string scOutput)
    {
        if (string.IsNullOrWhiteSpace(scOutput)) return "";
        var hints = new List<string>();
        var seen = new HashSet<int>();

        foreach (Match m in Regex.Matches(scOutput, @"\b(?:FAILED|ошибка)\s*[:\s]?\s*(\d{1,5})\b", RegexOptions.IgnoreCase))
        {
            if (!int.TryParse(m.Groups[1].Value, out var code) || !seen.Add(code)) continue;
            var hint = code switch
            {
                2 => "Код 2 — не найден файл агента по указанному пути. Проверьте, что Ypmon.Agent.exe на месте.",
                5 => "Код 5 — отказано в доступе. Подтвердите запрос UAC и запускайте агента от имени администратора.",
                87 => "Код 87 — неверный параметр. Обычно причина в имени службы (пробелы/спецсимволы) или в пути к файлу.",
                1053 => "Код 1053 — служба не ответила при запуске вовремя. Проверьте журнал Windows (Application).",
                1056 => "Код 1056 — служба уже запущена.",
                1057 => "Код 1057 — неверное имя учётной записи или пароль.",
                1058 => "Код 1058 — служба отключена. Включите её (тип запуска «Авто») и повторите.",
                1060 => "Код 1060 — служба не установлена. Создание службы не удалось (или её ещё не создавали) — " +
                        "нажмите «Установить / переустановить» и посмотрите текст ошибки на шаге «Создание службы».",
                1069 => "Код 1069 — не удалось войти под указанной учётной записью. Проверьте пароль и выдайте ей право " +
                        "«Вход в качестве службы» (secpol.msc → Локальные политики → Назначение прав пользователя).",
                1072 => "Код 1072 — служба помечена на удаление. Закройте оснастку «Службы» (services.msc) и повторите; " +
                        "если не помогает — перезагрузите сервер.",
                1073 => "Код 1073 — служба с таким именем уже существует. Удалите её и установите заново.",
                _ => ""
            };
            if (hint.Length > 0) hints.Add(hint);
        }

        if (scOutput.Contains("does not exist", StringComparison.OrdinalIgnoreCase) && seen.Count == 0)
            hints.Add("Служба с таким именем не установлена.");

        return hints.Count == 0 ? "" : string.Join("\n", hints);
    }

    /// <summary>Экранирует значение для вставки в .cmd: % в батнике раскрывается как переменная.</summary>
    private static string Bat(string s) => s.Replace("%", "%%");

    private static string RunElevated(string script)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "ypmon_svc_" + Guid.NewGuid().ToString("N"));
        var logFile = tmp + ".log";
        var cmdFile = tmp + ".cmd";
        File.WriteAllText(cmdFile, script.Replace("%LOG%", logFile), new UTF8Encoding(false));

        try
        {
            var psi = new ProcessStartInfo("cmd.exe", $"/c \"{cmdFile}\"")
            {
                UseShellExecute = true,
                Verb = "runas",                 // запрос повышения прав (UAC)
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi)!;
            p.WaitForExit(60000);
        }
        catch (Win32Exception)
        {
            CleanupSafe(cmdFile, logFile);
            return "Операция отменена или нет прав администратора (запрос UAC не подтверждён).";
        }

        var output = File.Exists(logFile) ? File.ReadAllText(logFile, Encoding.UTF8).Trim() : "(нет вывода)";
        CleanupSafe(cmdFile, logFile);
        return string.IsNullOrWhiteSpace(output) ? "Готово." : output;
    }

    private static void CleanupSafe(params string[] files)
    {
        foreach (var f in files) try { if (File.Exists(f)) File.Delete(f); } catch { }
    }
}
