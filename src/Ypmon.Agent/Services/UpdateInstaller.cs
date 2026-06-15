using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;

namespace Ypmon.Agent.Services;

/// <summary>
/// Самообновление агента: проверка версии на сервере, скачивание нового exe и установка
/// через внешний скрипт (останавливает службу/процесс, заменяет файл, перезапускает).
/// </summary>
public static class UpdateInstaller
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    public sealed record VersionInfo(bool Available, string? Version);

    public static async Task<VersionInfo> CheckAsync(AgentConfig cfg)
    {
        if (string.IsNullOrWhiteSpace(cfg.ServerUrl) || string.IsNullOrWhiteSpace(cfg.ApiKey))
            return new VersionInfo(false, null);

        using var req = new HttpRequestMessage(HttpMethod.Get, cfg.ServerUrl.TrimEnd('/') + "/api/agent/version");
        req.Headers.Add("X-Api-Key", cfg.ApiKey);
        var resp = await Http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        var info = await resp.Content.ReadFromJsonAsync<VersionInfo>();
        return info ?? new VersionInfo(false, null);
    }

    public static bool IsNewer(string? serverVersion, string currentVersion)
    {
        if (!Version.TryParse(serverVersion, out var sv)) return false;
        if (!Version.TryParse(currentVersion, out var cv)) return false;
        return sv > cv;
    }

    /// <summary>Скачивает новый exe во временный файл рядом с текущим. Возвращает путь.</summary>
    public static async Task<string> DownloadAsync(AgentConfig cfg)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, cfg.ServerUrl.TrimEnd('/') + "/api/agent/download");
        req.Headers.Add("X-Api-Key", cfg.ApiKey);
        var resp = await Http.SendAsync(req);
        resp.EnsureSuccessStatusCode();

        var dir = Path.GetDirectoryName(ServiceManager.ExePath)!;
        var updatePath = Path.Combine(dir, "Ypmon.Agent.update.exe");
        await using (var fs = File.Create(updatePath))
            await resp.Content.CopyToAsync(fs);
        return updatePath;
    }

    /// <summary>
    /// Запускает внешний апдейтер и возвращает управление. Вызывающий должен затем завершить процесс,
    /// чтобы файл разблокировался: в режиме службы апдейтер сам остановит/запустит службу,
    /// в интерактивном — дождётся выхода и перезапустит exe.
    /// </summary>
    public static void ApplyAndExit(string serviceName, bool isService, string updateExe)
    {
        var target = ServiceManager.ExePath;
        var dir = Path.GetDirectoryName(target)!;
        var cmdFile = Path.Combine(dir, "ypmon_update.cmd");

        var s = new StringBuilder();
        s.AppendLine("@echo off");
        s.AppendLine("chcp 65001>nul");
        s.AppendLine("set /a tries=0");
        if (isService)
            s.AppendLine($"sc stop \"{serviceName}\" >nul 2>&1");
        s.AppendLine(":retry");
        s.AppendLine("ping -n 2 127.0.0.1 >nul");
        s.AppendLine($"copy /y \"{updateExe}\" \"{target}\" >nul 2>&1");
        s.AppendLine("if errorlevel 1 (");
        s.AppendLine("  set /a tries+=1");
        s.AppendLine("  if %tries% LSS 30 goto retry");
        s.AppendLine(")");
        if (isService)
            s.AppendLine($"sc start \"{serviceName}\" >nul 2>&1");
        else
            s.AppendLine($"start \"\" \"{target}\"");
        s.AppendLine($"del \"{updateExe}\" >nul 2>&1");
        s.AppendLine("del \"%~f0\" >nul 2>&1");

        File.WriteAllText(cmdFile, s.ToString(), new UTF8Encoding(false));

        Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{cmdFile}\"")
        {
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            CreateNoWindow = true
        });
    }
}
