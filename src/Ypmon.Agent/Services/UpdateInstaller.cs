using System.Diagnostics;
using System.Net.Http.Json;

namespace Ypmon.Agent.Services;

/// <summary>
/// Обновление агента через установщик: проверка версии на сервере, скачивание установщика
/// (YpmonAgent-Setup.exe) и его запуск. Установщик сам останавливает службу, заменяет файлы и
/// запускает службу заново — надёжнее самоподмены exe.
/// </summary>
public static class UpdateInstaller
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

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

    /// <summary>Скачивает установщик во временную папку и возвращает путь.</summary>
    public static async Task<string> DownloadAsync(AgentConfig cfg, IProgress<long>? progress = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, cfg.ServerUrl.TrimEnd('/') + "/api/agent/download");
        req.Headers.Add("X-Api-Key", cfg.ApiKey);
        var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();

        var path = Path.Combine(Path.GetTempPath(), "YpmonAgent-Setup.exe");
        await using (var src = await resp.Content.ReadAsStreamAsync())
        await using (var dst = File.Create(path))
        {
            var buf = new byte[81920];
            long total = 0; int read;
            while ((read = await src.ReadAsync(buf)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, read));
                total += read;
                progress?.Report(total);
            }
        }
        return path;
    }

    /// <summary>
    /// Запускает установщик.
    ///
    /// silent=true — авто-обновление из службы. Установщик сам останавливает службу, заменяет файлы
    /// и запускает её заново. Запускаем его ОТДЕЛЁННО от процесса службы (через `cmd /c start`):
    /// установщик — не дочерний процесс службы, поэтому её остановка не завершает установщик на
    /// половине. Раньше установщик стартовал как дочерний, а служба тут же себя останавливала —
    /// установщик мог погибнуть вместе с ней, оставив службу остановленной на старой версии.
    /// Агент себя НЕ останавливает: остановкой службы управляет сам установщик.
    ///
    /// silent=false — интерактивно из окна: через shell, чтобы показать запрос UAC пользователю.
    /// </summary>
    public static void RunInstaller(string installerPath, bool silent)
    {
        if (silent)
        {
            const string args = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /NOCANCEL";
            var psi = new ProcessStartInfo("cmd.exe", $"/c start \"YPMonUpdate\" /b \"{installerPath}\" {args}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            Process.Start(psi);
        }
        else
        {
            var psi = new ProcessStartInfo(installerPath, "/SILENT /NORESTART")
            {
                UseShellExecute = true,   // окно → через shell с запросом UAC
                WindowStyle = ProcessWindowStyle.Normal
            };
            Process.Start(psi);
        }
    }
}
