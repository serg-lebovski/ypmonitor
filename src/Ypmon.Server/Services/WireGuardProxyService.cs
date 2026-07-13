using System.Diagnostics;
using System.Text;

namespace Ypmon.Server.Services;

/// <summary>
/// Поднимает userspace-туннель к WireGuard / AmneziaWG (Amnezia VPN) и отдаёт SOCKS5-прокси,
/// через который бот ходит в Telegram. Без прав root/NET_ADMIN и модуля ядра.
///
/// Поддержка AmneziaWG: если в конфиге есть параметры обфускации (Jc, Jmin, Jmax, S1, S2, H1..H4),
/// они передаются движку как есть. Если используемый бинарник их не понимает и падает —
/// выполняется автоматический откат к «чистому» WireGuard (параметры обфускации убираются).
///
/// Приоритет бинарников: awgproxy (AmneziaWG-совместимый) → wireproxy. Путь можно задать
/// переменной окружения YPMON_WG_PROXY_BIN или положить бинарник в папку wg рядом с сервером.
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
    public string LastStatus { get; private set; } = "выключен";

    private static readonly string[] AmneziaKeys = { "Jc", "Jmin", "Jmax", "S1", "S2", "H1", "H2", "H3", "H4" };

    public WireGuardProxyService(IWebHostEnvironment env, ILogger<WireGuardProxyService> log)
    {
        _log = log;
        _dir = Path.Combine(env.ContentRootPath, "wg");
        _confPath = Path.Combine(_dir, "wireproxy.conf");
    }

    public bool IsRunning
    {
        get { lock (_lock) return _proc is { HasExited: false }; }
    }

    private string? FindBinary()
    {
        var env = Environment.GetEnvironmentVariable("YPMON_WG_PROXY_BIN");
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(env)) candidates.Add(env!);
        candidates.AddRange(new[]
        {
            Path.Combine(_dir, "awgproxy"), Path.Combine(_dir, "wireproxy"),
            "/usr/local/bin/awgproxy", "/usr/bin/awgproxy",
            "/usr/local/bin/wireproxy", "/usr/bin/wireproxy"
        });
        return candidates.FirstOrDefault(File.Exists);
    }

    public static bool HasAmneziaParams(string config)
    {
        foreach (var line in config.Split('\n'))
        {
            var t = line.TrimStart();
            foreach (var k in AmneziaKeys)
                if (t.StartsWith(k + " ", StringComparison.OrdinalIgnoreCase) ||
                    t.StartsWith(k + "=", StringComparison.OrdinalIgnoreCase))
                    return true;
        }
        return false;
    }

    private static string StripAmnezia(string config)
    {
        var sb = new StringBuilder();
        foreach (var line in config.Split('\n'))
        {
            var t = line.TrimStart();
            var drop = AmneziaKeys.Any(k =>
                t.StartsWith(k + " ", StringComparison.OrdinalIgnoreCase) ||
                t.StartsWith(k + "=", StringComparison.OrdinalIgnoreCase));
            if (!drop) sb.Append(line).Append('\n');
        }
        return sb.ToString();
    }

    private void WriteConf(string wgConfig)
    {
        Directory.CreateDirectory(_dir);
        var conf = wgConfig.TrimEnd() + "\n\n[Socks5]\nBindAddress = 127.0.0.1:" + SocksPort + "\n";
        File.WriteAllText(_confPath, conf);
    }

    /// <summary>Применяет настройки: (пере)запускает или останавливает туннель.</summary>
    public void Apply(bool enabled, string? wgConfig)
    {
        lock (_lock)
        {
            Stop();
            if (!enabled || string.IsNullOrWhiteSpace(wgConfig))
            {
                try { if (File.Exists(_confPath)) File.Delete(_confPath); } catch { }
                LastStatus = "выключен";
                return;
            }

            var bin = FindBinary();
            if (bin is null)
            {
                LastStatus = "бинарник wireproxy/awgproxy не найден";
                _log.LogWarning("WireGuard включён, но прокси-бинарник не найден.");
                return;
            }

            var amnezia = HasAmneziaParams(wgConfig);

            // Первая попытка — как есть (с параметрами AmneziaWG, если они есть).
            if (TryStart(bin, wgConfig))
            {
                LastStatus = amnezia ? "туннель поднят (AmneziaWG)" : "туннель поднят (WireGuard)";
                return;
            }

            // Если был Amnezia-конфиг и движок его не принял — откат к чистому WireGuard.
            if (amnezia)
            {
                _log.LogWarning("Движок не принял параметры AmneziaWG — пробуем как обычный WireGuard.");
                if (TryStart(bin, StripAmnezia(wgConfig)))
                {
                    LastStatus = "туннель поднят (WireGuard, без обфускации AmneziaWG — нужен awgproxy)";
                    return;
                }
            }

            LastStatus = "не удалось поднять туннель (см. журнал сервера)";
        }
    }

    private bool TryStart(string bin, string wgConfig)
    {
        Stop();
        try
        {
            WriteConf(wgConfig);
            var errBuf = new StringBuilder();
            var p = new Process
            {
                StartInfo = new ProcessStartInfo(bin, $"-c \"{_confPath}\"")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            // ВАЖНО: асинхронно вычитываем потоки. Иначе wireproxy (подробный DEBUG-вывод) забивает
            // буфер OS-пайпа и БЛОКИРУЕТСЯ — туннель перестаёт передавать трафик («SOCKS failed to connect»).
            p.OutputDataReceived += (_, _) => { };
            p.ErrorDataReceived += (_, e) => { if (e.Data is not null && errBuf.Length < 4000) errBuf.AppendLine(e.Data); };
            if (!p.Start()) { return false; }
            _proc = p;
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            // Даём процессу подняться; если сразу упал (напр. не понял конфиг) — считаем неудачей.
            Thread.Sleep(1500);
            if (_proc.HasExited)
            {
                _log.LogWarning("Прокси-процесс завершился: {Err}", errBuf.ToString());
                _proc = null;
                return false;
            }
            _log.LogInformation("Туннель поднят: {Bin} (SOCKS5 127.0.0.1:{Port}).", bin, SocksPort);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Не удалось запустить прокси-бинарник {Bin}", bin);
            _proc = null;
            return false;
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
