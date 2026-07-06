using System.Diagnostics;

namespace Ypmon.Server.Services;

/// <summary>
/// Поднимает userspace-WireGuard (wireproxy) и отдаёт SOCKS5-прокси, через который бот ходит
/// в Telegram. Не требует прав root/NET_ADMIN и модуля ядра. Если бинарник wireproxy отсутствует
/// (напр. на Windows-разработке) — молча выключается, Telegram работает напрямую/через обычный прокси.
/// </summary>
public sealed class WireGuardProxyService : IDisposable
{
    private readonly ILogger<WireGuardProxyService> _log;
    private readonly string _dir;
    private readonly string _confPath;
    private readonly object _lock = new();
    private Process? _proc;

    public const int SocksPort = 25344;
    public string SocksProxyUrl => $"socks5://127.0.0.1:{SocksPort}";

    public bool IsRunning
    {
        get { lock (_lock) return _proc is { HasExited: false }; }
    }

    public WireGuardProxyService(IWebHostEnvironment env, ILogger<WireGuardProxyService> log)
    {
        _log = log;
        _dir = Path.Combine(env.ContentRootPath, "wg");
        _confPath = Path.Combine(_dir, "wireproxy.conf");
    }

    private static string? FindBinary()
    {
        foreach (var p in new[] { "/usr/local/bin/wireproxy", "/usr/bin/wireproxy" })
            if (File.Exists(p)) return p;
        return null;
    }

    /// <summary>Применяет настройки: (пере)запускает или останавливает wireproxy.</summary>
    public void Apply(bool enabled, string? wgConfig)
    {
        lock (_lock)
        {
            Stop();
            if (!enabled || string.IsNullOrWhiteSpace(wgConfig)) return;

            var bin = FindBinary();
            if (bin is null)
            {
                _log.LogWarning("WireGuard включён, но бинарник wireproxy не найден — Telegram пойдёт без туннеля.");
                return;
            }

            try
            {
                Directory.CreateDirectory(_dir);
                // wireproxy = стандартный конфиг WireGuard + секция [Socks5].
                var conf = wgConfig.TrimEnd() + "\n\n[Socks5]\nBindAddress = 127.0.0.1:" + SocksPort + "\n";
                File.WriteAllText(_confPath, conf);

                _proc = Process.Start(new ProcessStartInfo(bin, $"-c \"{_confPath}\"")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                });
                _log.LogInformation("wireproxy запущен (SOCKS5 на 127.0.0.1:{Port}).", SocksPort);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Не удалось запустить wireproxy");
                _proc = null;
            }
        }
    }

    private void Stop()
    {
        if (_proc is null) return;
        try { if (!_proc.HasExited) _proc.Kill(true); } catch { }
        try { _proc.Dispose(); } catch { }
        _proc = null;
    }

    public void Dispose() { lock (_lock) Stop(); }
}
