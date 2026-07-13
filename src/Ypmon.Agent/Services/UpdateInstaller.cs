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
    /// silent=true — тихо, для авто-обновления из службы: служба уже запущена с правами администратора
    /// (LocalSystem или админ-учётка), поэтому UseShellExecute=false — процесс наследует уже повышенный
    /// токен без запроса UAC (в сессии службы интерактивный UAC невозможен, из-за чего обновление «зависало»).
    /// silent=false — интерактивно из окна: UseShellExecute=true, чтобы показать запрос UAC пользователю.
    /// </summary>
    public static void RunInstaller(string installerPath, bool silent)
    {
        var args = silent
            ? "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /NOCANCEL"
            : "/SILENT /NORESTART";
        var psi = new ProcessStartInfo(installerPath, args)
        {
            UseShellExecute = !silent,   // служба уже elevated → без UAC; окно → через shell с UAC
            CreateNoWindow = silent,
            WindowStyle = silent ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal
        };
        Process.Start(psi);
    }
}
