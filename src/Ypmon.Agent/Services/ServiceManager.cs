using System.ComponentModel;
using System.Diagnostics;
using System.ServiceProcess;
using System.Text;

namespace Ypmon.Agent.Services;

/// <summary>
/// Установка/удаление/управление службой Windows для агента.
/// Действия, требующие прав администратора, выполняются через sc.exe с запросом UAC.
/// </summary>
public static class ServiceManager
{
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
    /// Устанавливает службу с заданным именем. Если указан account — служба запускается под ним
    /// (DOMAIN\User, .\User), иначе под LocalSystem.
    /// </summary>
    public static string Install(string name, string displayName, string? account, string? password)
    {
        var obj = string.IsNullOrWhiteSpace(account)
            ? ""
            : $"obj= \"{account}\" password= \"{password}\"";

        var script = new StringBuilder();
        script.AppendLine("@echo off");
        script.AppendLine("chcp 65001>nul");
        script.AppendLine($"sc stop \"{name}\" >nul 2>&1");
        script.AppendLine("ping -n 2 127.0.0.1 >nul");
        script.AppendLine($"sc delete \"{name}\" >nul 2>&1");
        script.AppendLine("ping -n 2 127.0.0.1 >nul");
        script.AppendLine($"sc create \"{name}\" binPath= \"{ExePath}\" start= auto DisplayName= \"{displayName}\" {obj} > \"%LOG%\" 2>&1");
        script.AppendLine($"sc description \"{name}\" \"Агент мониторинга и резервного копирования YPMon\" >> \"%LOG%\" 2>&1");
        script.AppendLine($"sc failure \"{name}\" reset= 60 actions= restart/5000/restart/5000/restart/5000 >> \"%LOG%\" 2>&1");
        script.AppendLine($"sc start \"{name}\" >> \"%LOG%\" 2>&1");
        return RunElevated(script.ToString());
    }

    public static string Uninstall(string name)
    {
        var script = new StringBuilder();
        script.AppendLine("@echo off");
        script.AppendLine("chcp 65001>nul");
        script.AppendLine($"sc stop \"{name}\" > \"%LOG%\" 2>&1");
        script.AppendLine("ping -n 3 127.0.0.1 >nul");
        script.AppendLine($"sc delete \"{name}\" >> \"%LOG%\" 2>&1");
        return RunElevated(script.ToString());
    }

    public static string Start(string name) => RunElevated($"@echo off\r\nchcp 65001>nul\r\nsc start \"{name}\" > \"%LOG%\" 2>&1\r\n");
    public static string Stop(string name) => RunElevated($"@echo off\r\nchcp 65001>nul\r\nsc stop \"{name}\" > \"%LOG%\" 2>&1\r\n");

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
            return "Операция отменена или нет прав администратора.";
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
