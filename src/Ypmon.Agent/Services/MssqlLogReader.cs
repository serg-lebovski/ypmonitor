using Ypmon.Shared;

namespace Ypmon.Agent.Services;

/// <summary>Чтение и разбор логов архивации MSSQL из указанной папки.</summary>
public static class MssqlLogReader
{
    public record LogFileInfo(string Name, string FullPath, DateTimeOffset Modified, long SizeBytes);

    public static List<LogFileInfo> ListLogs(MssqlLogConfig cfg)
    {
        var result = new List<LogFileInfo>();
        if (!cfg.Enabled || string.IsNullOrWhiteSpace(cfg.LogFolder) || !Directory.Exists(cfg.LogFolder))
            return result;

        var pattern = string.IsNullOrWhiteSpace(cfg.FilePattern) ? "*.txt" : cfg.FilePattern;
        foreach (var f in Directory.EnumerateFiles(cfg.LogFolder, pattern))
        {
            var fi = new FileInfo(f);
            result.Add(new LogFileInfo(fi.Name, fi.FullName, fi.LastWriteTimeUtc, fi.Length));
        }
        return result.OrderByDescending(x => x.Modified).ToList();
    }

    public static string ReadTail(string fullPath, int maxBytes = 64 * 1024)
    {
        try
        {
            using var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length > maxBytes) fs.Seek(-maxBytes, SeekOrigin.End);
            using var sr = new StreamReader(fs);
            return sr.ReadToEnd();
        }
        catch (Exception ex) { return $"Не удалось прочитать файл: {ex.Message}"; }
    }

    /// <summary>Грубый разбор статуса по содержимому лога.</summary>
    public static JobOutcome DetectOutcome(string content)
    {
        var lower = content.ToLowerInvariant();
        if (lower.Contains("error") || lower.Contains("ошиб") || lower.Contains("fail") || lower.Contains("сбой"))
            return JobOutcome.Error;
        if (lower.Contains("warning") || lower.Contains("предупрежд"))
            return JobOutcome.Warning;
        if (lower.Contains("success") || lower.Contains("успешно") || lower.Contains("completed") || lower.Contains("backup database"))
            return JobOutcome.Ok;
        return JobOutcome.Unknown;
    }

    /// <summary>Сводный статус задания MSSQL по самому свежему логу.</summary>
    public static JobStatusDto BuildStatus(MssqlLogConfig cfg)
    {
        var status = new JobStatusDto { Name = "Логи MSSQL", Type = JobType.MssqlLog, Target = cfg.LogFolder };
        var logs = ListLogs(cfg);
        status.BackupCount = logs.Count;
        if (logs.Count == 0)
        {
            status.Outcome = JobOutcome.Unknown;
            status.Message = cfg.Enabled ? "Логи не найдены" : "Отключено";
            return status;
        }

        var latest = logs.First();
        status.LastRunAt = latest.Modified;
        status.LastBackupAt = latest.Modified;
        status.TotalSizeBytes = logs.Sum(l => l.SizeBytes);
        var content = ReadTail(latest.FullPath);
        status.Outcome = DetectOutcome(content);
        status.Message = $"Свежий лог: {latest.Name} ({status.Outcome})";
        return status;
    }
}
