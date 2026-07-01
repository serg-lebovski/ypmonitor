using System.Diagnostics.Eventing.Reader;
using System.Runtime.Versioning;
using Ypmon.Shared;

namespace Ypmon.Agent.Services;

/// <summary>
/// Читает журнал событий Windows (System, Application и т.п.) и собирает новые ошибки/предупреждения
/// с прошлого отчёта. Только чтение. Отметка прогресса — RecordId по каждому журналу (в eventlog-state.json),
/// чтобы не отправлять одно и то же повторно.
/// </summary>
[SupportedOSPlatform("windows")]
public class EventLogReaderService
{
    private readonly ConfigStore _store;

    public EventLogReaderService(ConfigStore store) => _store = store;

    public List<EventLogEntryDto> Collect(AgentConfig cfg)
    {
        var result = new List<EventLogEntryDto>();
        var el = cfg.EventLog;
        if (el is null || !el.Enabled || !OperatingSystem.IsWindows())
            return result;

        var watermarks = _store.LoadEventLogState();
        var logs = (el.Logs is { Count: > 0 } ? el.Logs : new List<string> { "System", "Application" })
            .Select(l => l.Trim()).Where(l => l.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        var maxPerLog = Math.Max(1, el.MaxEntriesPerReport);
        var levelFilter = el.IncludeWarnings ? "(Level=1 or Level=2 or Level=3)" : "(Level=1 or Level=2)";
        var exclude = new HashSet<string>(el.ExcludeSources ?? new(), StringComparer.OrdinalIgnoreCase);

        foreach (var logName in logs)
        {
            try
            {
                watermarks.TryGetValue(logName, out var lastId);

                string xpath;
                if (lastId <= 0)
                {
                    var ms = Math.Max(1, el.LookbackHoursOnFirstRun) * 3600L * 1000L;
                    xpath = $"*[System[{levelFilter} and TimeCreated[timediff(@SystemTime) <= {ms}]]]";
                }
                else
                {
                    xpath = $"*[System[{levelFilter} and EventRecordID > {lastId}]]";
                }

                var query = new EventLogQuery(logName, PathType.LogName, xpath) { ReverseDirection = true };
                using var reader = new EventLogReader(query);

                var maxSeen = lastId;
                var taken = 0;
                for (var rec = reader.ReadEvent(); rec is not null; rec = reader.ReadEvent())
                {
                    using (rec)
                    {
                        var rid = rec.RecordId ?? 0;
                        if (rid > maxSeen) maxSeen = rid;
                        if (taken >= maxPerLog) continue; // дочитываем ради корректной отметки, но не копим

                        var src = rec.ProviderName ?? "";
                        if (exclude.Contains(src)) continue;

                        string msg;
                        try { msg = rec.FormatDescription() ?? ""; } catch { msg = ""; }
                        if (string.IsNullOrWhiteSpace(msg)) msg = $"(событие {rec.Id})";
                        msg = msg.Trim();
                        if (msg.Length > 2000) msg = msg[..2000] + "…";

                        var time = rec.TimeCreated is { } dt
                            ? new DateTimeOffset(dt.ToUniversalTime(), TimeSpan.Zero)
                            : DateTimeOffset.UtcNow;

                        result.Add(new EventLogEntryDto
                        {
                            LogName = logName,
                            Source = src,
                            Level = LevelName(rec.Level),
                            EventId = rec.Id,
                            TimeCreated = time,
                            Message = msg
                        });
                        taken++;
                    }
                }

                watermarks[logName] = maxSeen;
            }
            catch
            {
                // Журнал недоступен или нет прав на чтение — пропускаем, не роняя отчёт.
            }
        }

        _store.SaveEventLogState(watermarks);
        return result.OrderByDescending(e => e.TimeCreated).ToList();
    }

    /// <summary>Предпросмотр последних записей без сдвига отметки прочитанного (для кнопки в окне).</summary>
    public List<EventLogEntryDto> PreviewRecent(AgentConfig cfg, int count)
    {
        var result = new List<EventLogEntryDto>();
        var el = cfg.EventLog;
        if (el is null || !OperatingSystem.IsWindows()) return result;

        var logs = (el.Logs is { Count: > 0 } ? el.Logs : new List<string> { "System", "Application" })
            .Select(l => l.Trim()).Where(l => l.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase);
        var levelFilter = el.IncludeWarnings ? "(Level=1 or Level=2 or Level=3)" : "(Level=1 or Level=2)";
        var exclude = new HashSet<string>(el.ExcludeSources ?? new(), StringComparer.OrdinalIgnoreCase);
        var ms = Math.Max(1, el.LookbackHoursOnFirstRun) * 3600L * 1000L;

        foreach (var logName in logs)
        {
            try
            {
                var xpath = $"*[System[{levelFilter} and TimeCreated[timediff(@SystemTime) <= {ms}]]]";
                var query = new EventLogQuery(logName, PathType.LogName, xpath) { ReverseDirection = true };
                using var reader = new EventLogReader(query);
                var taken = 0;
                for (var rec = reader.ReadEvent(); rec is not null && taken < count; rec = reader.ReadEvent())
                {
                    using (rec)
                    {
                        var src = rec.ProviderName ?? "";
                        if (exclude.Contains(src)) continue;
                        string msg;
                        try { msg = rec.FormatDescription() ?? ""; } catch { msg = ""; }
                        if (string.IsNullOrWhiteSpace(msg)) msg = $"(событие {rec.Id})";
                        result.Add(new EventLogEntryDto
                        {
                            LogName = logName,
                            Source = src,
                            Level = LevelName(rec.Level),
                            EventId = rec.Id,
                            TimeCreated = rec.TimeCreated is { } dt ? new DateTimeOffset(dt.ToUniversalTime(), TimeSpan.Zero) : DateTimeOffset.UtcNow,
                            Message = msg.Trim()
                        });
                        taken++;
                    }
                }
            }
            catch { }
        }
        return result.OrderByDescending(e => e.TimeCreated).Take(count).ToList();
    }

    private static string LevelName(byte? level) => level switch
    {
        1 => "Critical",
        2 => "Error",
        3 => "Warning",
        _ => "Error"
    };
}
