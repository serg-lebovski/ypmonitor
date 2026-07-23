using System.Text.Json;
using Ypmon.Server.Data;

namespace Ypmon.Server.Services;

/// <summary>Тип тревоги — нужен, чтобы окна обслуживания глушили только часть тревог, а snooze — все.</summary>
public enum AlertKind
{
    Offline,      // пропала связь с агентом
    Ping,         // не отвечает внешний IP сервера
    DiskLow,      // мало места на диске
    DiskHealth,   // ухудшение SMART / перегрев накопителя
    CpuOverheat,  // перегрев процессора
    Reboot        // перезагрузка/выключение (на будущее)
}

/// <summary>Повторяющееся окно обслуживания: дни недели по Омску + интервал времени суток.</summary>
public sealed record MaintWindow(int[] Days, int StartMin, int EndMin);

/// <summary>
/// Тихий режим: решает, глушить ли тревогу по конкретному серверу.
///  • Snooze (ручная заглушка «знаю про проблему») — глушит ВСЕ тревоги сервера до истечения срока.
///  • Окна обслуживания (плановые) — глушат только «шумовые» тревоги (офлайн, ping, перезагрузка),
///    а нехватку места и здоровье диска не трогают — они не про обслуживание.
/// Подавляется только ОТПРАВКА; состояние тревоги на сервере отслеживается как обычно,
/// поэтому в интерфейсе и ежедневном отчёте проблема остаётся видна.
/// </summary>
public static class AlertSuppression
{
    /// <summary>Какие тревоги подчиняются окну обслуживания (остальные — только ручному snooze).</summary>
    private static bool WindowApplies(AlertKind kind) =>
        kind is AlertKind.Offline or AlertKind.Ping or AlertKind.Reboot;

    public static bool IsSuppressed(MonitoredServer srv, AlertKind kind, DateTimeOffset nowUtc, out string reason)
    {
        reason = "";

        // Ручная заглушка — на весь сервер, до истечения срока.
        if (srv.AlertSnoozeUntil is { } until && until > nowUtc)
        {
            reason = $"заглушено до {UiHelpers.Omsk(until, "dd.MM HH:mm")}";
            return true;
        }

        if (WindowApplies(kind) && InMaintenanceWindow(srv, nowUtc))
        {
            reason = "окно обслуживания";
            return true;
        }

        return false;
    }

    /// <summary>Сейчас (по Омску) действует какое-либо окно обслуживания этого сервера.</summary>
    public static bool InMaintenanceWindow(MonitoredServer srv, DateTimeOffset nowUtc)
    {
        var windows = ParseWindows(srv.MaintenanceWindowsJson);
        if (windows.Count == 0) return false;

        var omsk = nowUtc.UtcDateTime.AddHours(6);   // сервер живёт в Омске (UTC+6)
        var day = (int)omsk.DayOfWeek;               // 0 = воскресенье
        var minute = omsk.Hour * 60 + omsk.Minute;

        foreach (var w in windows)
        {
            if (!w.Days.Contains(day)) continue;
            // Окно, переходящее через полночь (конец ≤ начала), считаем как [начало..24:00) ∪ [00:00..конец).
            var inside = w.EndMin > w.StartMin
                ? minute >= w.StartMin && minute < w.EndMin
                : minute >= w.StartMin || minute < w.EndMin;
            if (inside) return true;
        }
        return false;
    }

    /// <summary>Причина, по которой сервер сейчас «молчит» (для пометки в UI и отчёте), либо null.</summary>
    public static string? MuteReason(MonitoredServer srv, DateTimeOffset nowUtc)
    {
        if (srv.AlertSnoozeUntil is { } until && until > nowUtc)
            return $"заглушено до {UiHelpers.Omsk(until, "dd.MM HH:mm")}";
        if (InMaintenanceWindow(srv, nowUtc))
            return "идёт окно обслуживания";
        return null;
    }

    public static List<MaintWindow> ParseWindows(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try
        {
            var raw = JsonSerializer.Deserialize<List<MaintWindow>>(json!);
            return raw?.Where(w => w.Days is { Length: > 0 }
                                && w.StartMin is >= 0 and < 1440
                                && w.EndMin is >= 0 and < 1440
                                && w.StartMin != w.EndMin)
                      .ToList() ?? new();
        }
        catch { return new(); }
    }

    public static string SerializeWindows(IEnumerable<MaintWindow> windows) =>
        JsonSerializer.Serialize(windows.ToList());
}
