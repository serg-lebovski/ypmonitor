using System.Diagnostics.Eventing.Reader;
using System.Management;
using System.Runtime.Versioning;
using Ypmon.Shared;

namespace Ypmon.Agent.Services;

/// <summary>
/// Аптайм и история перезагрузок из журнала System. Только чтение, best-effort.
/// Отметку о прочитанном не храним: агент каждый полный отчёт присылает окно за последние
/// недели, а сервер сам отбрасывает уже известные события. Так история восстанавливается
/// даже после переустановки агента или потери его состояния.
/// </summary>
[SupportedOSPlatform("windows")]
public static class RebootHistoryCollector
{
    /// <summary>За сколько дней присылать события (одно включение = 1–2 записи, объём небольшой).</summary>
    public const int LookbackDays = 45;

    /// <summary>Верхняя граница на случай машины, которая перезагружается постоянно.</summary>
    private const int MaxEvents = 120;

    /// <summary>Время последней загрузки машины (UTC) или null, если WMI недоступен.</summary>
    public static DateTimeOffset? LastBootAt()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT LastBootUpTime FROM Win32_OperatingSystem");
            foreach (ManagementObject mo in searcher.Get())
            {
                var raw = mo["LastBootUpTime"]?.ToString();
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var local = ManagementDateTimeConverter.ToDateTime(raw);
                return new DateTimeOffset(local.ToUniversalTime(), TimeSpan.Zero);
            }
        }
        catch { }
        return null;
    }

    public static List<RebootEventDto> Collect()
    {
        var result = new List<RebootEventDto>();
        try
        {
            var ms = LookbackDays * 24L * 3600L * 1000L;
            // 6005 — журнал событий запущен (машина включилась), 6006 — журнал остановлен (штатное выключение),
            // 6008 — предыдущее выключение было аварийным, 1074 — выключение/перезагрузку кто-то инициировал,
            // 41 (Kernel-Power) — система перезагрузилась без корректного завершения работы.
            var xpath = "*[System[(EventID=6005 or EventID=6006 or EventID=6008 or EventID=1074 or EventID=41)"
                      + $" and TimeCreated[timediff(@SystemTime) <= {ms}]]]";

            var query = new EventLogQuery("System", PathType.LogName, xpath) { ReverseDirection = true };
            using var reader = new EventLogReader(query);

            for (var rec = reader.ReadEvent(); rec is not null && result.Count < MaxEvents; rec = reader.ReadEvent())
            {
                using (rec)
                {
                    var at = rec.TimeCreated is { } dt
                        ? new DateTimeOffset(dt.ToUniversalTime(), TimeSpan.Zero)
                        : DateTimeOffset.UtcNow;

                    string? reason = null;
                    if (rec.Id is 1074)
                    {
                        // В 1074 полезен сам текст: там и процесс-инициатор, и причина, и пользователь.
                        try { reason = rec.FormatDescription(); } catch { }
                        reason = Shorten(reason);
                    }

                    result.Add(new RebootEventDto
                    {
                        At = at,
                        Kind = rec.Id switch
                        {
                            6005 => "Startup",
                            6006 => "Shutdown",
                            6008 => "Unexpected",
                            41 => "Unexpected",
                            _ => "Initiated"
                        },
                        Reason = reason
                    });
                }
            }
        }
        catch
        {
            // Журнал недоступен или нет прав — отчёт из-за этого ронять нельзя.
        }
        return result;
    }

    /// <summary>Из длинного описания события берём первую осмысленную строку.</summary>
    private static string? Shorten(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var line = text.Replace("\r", " ").Replace("\n", " ").Trim();
        while (line.Contains("  ")) line = line.Replace("  ", " ");
        return line.Length > 300 ? line[..300] + "…" : line;
    }
}
