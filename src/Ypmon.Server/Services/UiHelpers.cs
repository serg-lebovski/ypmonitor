using System.Text.Json;
using Ypmon.Shared;

namespace Ypmon.Server.Services;

public static class UiHelpers
{
    /// <summary>Сводка по бэкапам из сохранённого отчёта: дата последней копии и суммарный объём.</summary>
    public static (DateTimeOffset? lastBackupAt, long sizeBytes) BackupSummary(string? reportJson)
    {
        if (string.IsNullOrWhiteSpace(reportJson)) return (null, 0);
        try
        {
            var r = JsonSerializer.Deserialize<AgentReportDto>(reportJson);
            if (r is null) return (null, 0);
            DateTimeOffset? last = r.Jobs.Where(j => j.LastBackupAt != null)
                .Select(j => j.LastBackupAt).DefaultIfEmpty(null).Max();
            return (last, r.Jobs.Sum(j => j.TotalSizeBytes));
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

    public static string JobTypeText(JobType t) => t switch
    {
        JobType.PostgresBackup => "Бэкап PostgreSQL",
        JobType.FileArchive => "Архивация файлов",
        JobType.MssqlLog => "Логи MSSQL",
        _ => t.ToString()
    };

    public static string Bytes(long b)
    {
        string[] u = { "Б", "КБ", "МБ", "ГБ", "ТБ" };
        double v = b; int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return $"{v:0.#} {u[i]}";
    }

    public static string Ago(DateTimeOffset? t)
    {
        if (t is null) return "никогда";
        var d = DateTimeOffset.UtcNow - t.Value;
        if (d.TotalSeconds < 60) return "только что";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes} мин назад";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours} ч назад";
        return $"{(int)d.TotalDays} дн назад";
    }
}
