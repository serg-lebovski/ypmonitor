using System.Security.Claims;
using System.Text.Json;
using Ypmon.Server.Data;
using Ypmon.Shared;

namespace Ypmon.Server.Services;

/// <summary>Права ролей. Admin — всё; Engineer — клиенты/серверы (без настроек сервера); Viewer — только просмотр.</summary>
public static class RolePermissions
{
    /// <summary>Может добавлять/редактировать/удалять клиентов и серверы (Администратор или Инженер).</summary>
    public static bool CanEdit(this ClaimsPrincipal u) => u.IsInRole("Admin") || u.IsInRole("Engineer");

    /// <summary>Полный доступ, включая настройки сервера и пользователей.</summary>
    public static bool CanAdmin(this ClaimsPrincipal u) => u.IsInRole("Admin");
}

public static class UiHelpers
{
    public static string RoleName(string? role) => role switch
    {
        "Admin" => "Администратор",
        "Engineer" => "Инженер",
        _ => "Мониторинг"
    };

    public static string RoleName(UserRole r) => r switch
    {
        UserRole.Admin => "Администратор",
        UserRole.Engineer => "Инженер",
        _ => "Мониторинг"
    };


    /// <summary>Сводка по бэкапам из сохранённого отчёта: дата последней копии и суммарный объём.</summary>
    public static (DateTimeOffset? lastBackupAt, long sizeBytes) BackupSummary(string? reportJson)
    {
        if (string.IsNullOrWhiteSpace(reportJson)) return (null, 0);
        try
        {
            var r = JsonSerializer.Deserialize<AgentReportDto>(reportJson);
            if (r is null) return (null, 0);
            DateTimeOffset? last = r.Folders.Where(f => f.LastBackupAt != null)
                .Select(f => f.LastBackupAt).DefaultIfEmpty(null).Max();
            return (last, r.Folders.Sum(f => f.TotalSizeBytes));
        }
        catch { return (null, 0); }
    }

    /// <summary>Абсолютные дата и время (UTC) для отображения.</summary>
    public static string DateTimeAbs(DateTimeOffset? t)
        => t is null ? "—" : t.Value.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss") + " UTC";

    public static string OutcomeCss(JobOutcome o) => o switch
    {
        JobOutcome.Ok => "st-ok",
        JobOutcome.Warning => "st-warn",
        JobOutcome.Error => "st-err",
        _ => "st-unknown"
    };

    public static string DotCss(JobOutcome o) => o switch
    {
        JobOutcome.Ok => "ok",
        JobOutcome.Warning => "warn",
        JobOutcome.Error => "err",
        _ => "unknown"
    };

    public static string OutcomeText(JobOutcome o) => o switch
    {
        JobOutcome.Ok => "Всё ок",
        JobOutcome.Warning => "Предупреждение",
        JobOutcome.Error => "Ошибка",
        _ => "Нет данных"
    };

    /// <summary>
    /// Единое вычисление статуса сервера для плитки/строки дашборда. Используется и при первичной
    /// отрисовке, и в realtime-эндпоинте — чтобы страница и живое обновление не расходились.
    /// status: ok|problem|offline|unknown; cls — класс плитки; ico/word — иконка и подпись.
    /// </summary>
    public static (string status, string cls, string ico, string word) TileStatus(MonitoredServer s, int offlineThreshold)
    {
        if (s.IsOffline(offlineThreshold))
            return ("offline", "t-off", "⏸", "Офлайн");
        if (s.BackupShrinkActive)
            return ("problem", "t-warn", "⚠", "Объём упал");
        return s.LastOutcome switch
        {
            JobOutcome.Ok => ("ok", "t-ok", "✔", "В норме"),
            JobOutcome.Warning => ("problem", "t-warn", "⚠", "Предупреждение"),
            JobOutcome.Error => ("problem", "t-err", "✖", "Ошибка"),
            _ => ("unknown", "t-unk", "…", "Нет данных")
        };
    }

    /// <summary>
    /// Человекочитаемая причина статуса папки для показа в подсказке и текстом.
    /// Пустая строка — если всё в порядке. staleDays — действующий порог устаревания для сервера.
    /// </summary>
    public static string FolderReason(FolderStatusDto f, int staleDays)
    {
        if (!f.Accessible)
            return string.IsNullOrWhiteSpace(f.Message)
                ? "папка недоступна (не существует или нет доступа)"
                : f.Message!;
        if (f.FileCount == 0)
            return "в папке нет ни одного файла бэкапа";
        if (staleDays > 0 && f.LastBackupAt is { } last &&
            (DateTimeOffset.UtcNow - last).TotalDays > staleDays)
        {
            var days = (int)Math.Floor((DateTimeOffset.UtcNow - last).TotalDays);
            return $"нет новых файлов {days} дн. (порог устаревания {staleDays} дн.)";
        }
        return string.IsNullOrWhiteSpace(f.Message) ? "" : f.Message!;
    }

    /// <summary>Действующий порог устаревания сервера: свой, иначе глобальный по умолчанию.</summary>
    public static int EffectiveStaleDays(int serverStaleDays, int defaultStaleDays)
        => serverStaleDays > 0 ? serverStaleDays : defaultStaleDays;

    /// <summary>Действующий порог устаревания конкретной папки: свой, иначе порог сервера, иначе глобальный.</summary>
    public static int FolderStale(string folderName, Dictionary<string, int>? perFolder, int serverStaleDays, int defaultStaleDays)
    {
        if (perFolder is not null && perFolder.TryGetValue(folderName, out var v) && v > 0) return v;
        return EffectiveStaleDays(serverStaleDays, defaultStaleDays);
    }

    public static string Bytes(long b)
    {
        string[] u = { "Б", "КБ", "МБ", "ГБ", "ТБ" };
        double v = b; int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return $"{v:0.#} {u[i]}";
    }

    /// <summary>Интервал в секундах → «2 ч 30 мин», «45 сек».</summary>
    public static string Duration(int seconds)
    {
        if (seconds <= 0) return "—";
        var h = seconds / 3600; var m = seconds % 3600 / 60; var s = seconds % 60;
        var parts = new List<string>();
        if (h > 0) parts.Add($"{h} ч");
        if (m > 0) parts.Add($"{m} мин");
        if (s > 0 || parts.Count == 0) parts.Add($"{s} сек");
        return string.Join(" ", parts);
    }

    public static string Ago(DateTimeOffset? t) => Ago(t, DateTimeOffset.UtcNow);

    /// <summary>То же относительно заданного момента — для отчётов, которые формируются по снимку данных.</summary>
    public static string Ago(DateTimeOffset? t, DateTimeOffset now)
    {
        if (t is null) return "никогда";
        var d = now - t.Value;
        if (d.TotalSeconds < 60) return "только что";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes} мин назад";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours} ч назад";
        return $"{(int)d.TotalDays} дн назад";
    }

    /// <summary>Время по Омску (UTC+6) — в журналах показываем его, а не UTC: так понятнее.</summary>
    public static string Omsk(DateTimeOffset t, string format = "yyyy-MM-dd HH:mm:ss")
        => t.UtcDateTime.AddHours(6).ToString(format);
}
