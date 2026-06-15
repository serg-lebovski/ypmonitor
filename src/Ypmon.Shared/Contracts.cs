namespace Ypmon.Shared;

/// <summary>Тип задания резервного копирования на агенте.</summary>
public enum JobType
{
    PostgresBackup = 0,
    FileArchive = 1,
    MssqlLog = 2,
    /// <summary>Мониторинг готовой папки бэкапов (сделанных любой программой, напр. AOMEI).</summary>
    FolderMonitor = 3
}

/// <summary>Итог выполнения задания.</summary>
public enum JobOutcome
{
    Unknown = 0,
    Ok = 1,
    Warning = 2,
    Error = 3
}

/// <summary>Статус одного задания резервного копирования/архивации.</summary>
public class JobStatusDto
{
    public string Name { get; set; } = "";
    public JobType Type { get; set; }
    public JobOutcome Outcome { get; set; }
    public string? Message { get; set; }

    /// <summary>Когда задание последний раз отрабатывало.</summary>
    public DateTimeOffset? LastRunAt { get; set; }

    /// <summary>Время самой свежей резервной копии.</summary>
    public DateTimeOffset? LastBackupAt { get; set; }

    /// <summary>Сколько резервных копий сейчас хранится.</summary>
    public int BackupCount { get; set; }

    /// <summary>Суммарный размер хранящихся копий, байт.</summary>
    public long TotalSizeBytes { get; set; }

    /// <summary>Куда складываются копии/что архивируется.</summary>
    public string? Target { get; set; }
}

/// <summary>Информация о диске на машине агента.</summary>
public class DiskInfoDto
{
    public string Name { get; set; } = "";
    public long TotalBytes { get; set; }
    public long FreeBytes { get; set; }
    public double UsedPercent => TotalBytes == 0 ? 0 : Math.Round((TotalBytes - FreeBytes) * 100.0 / TotalBytes, 1);
}

/// <summary>
/// Полный отчёт, который агент отправляет на сервер (POST /api/report).
/// Идентификация — по заголовку X-Api-Key, привязанному к серверу клиента.
/// </summary>
public class AgentReportDto
{
    /// <summary>Имя машины, где работает агент.</summary>
    public string MachineName { get; set; } = "";

    /// <summary>Версия агента.</summary>
    public string AgentVersion { get; set; } = "";

    /// <summary>Момент формирования отчёта (по часам агента).</summary>
    public DateTimeOffset ReportedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// true — это лёгкий отчёт о доступности (heartbeat): сервер обновит только время связи
    /// и доступность, не затрагивая данные заданий. false — полный отчёт о состоянии.
    /// </summary>
    public bool IsHeartbeat { get; set; }

    /// <summary>Доступен ли наблюдаемый сервер БД (true = ок).</summary>
    public bool ServerAvailable { get; set; }

    /// <summary>Пояснение к доступности (например, ошибка подключения).</summary>
    public string? AvailabilityMessage { get; set; }

    /// <summary>Аптайм машины агента в секундах.</summary>
    public long UptimeSeconds { get; set; }

    /// <summary>Что архивируется и в каком состоянии — список заданий.</summary>
    public List<JobStatusDto> Jobs { get; set; } = new();

    /// <summary>Состояние дисков (для мониторинга свободного места).</summary>
    public List<DiskInfoDto> Disks { get; set; } = new();

    /// <summary>Итоговый статус (худший из заданий + доступность).</summary>
    public JobOutcome OverallOutcome
    {
        get
        {
            if (!ServerAvailable) return JobOutcome.Error;
            if (Jobs.Count == 0) return JobOutcome.Unknown;
            return Jobs.Max(j => j.Outcome);
        }
    }

    /// <summary>Суммарно копий по всем заданиям.</summary>
    public int TotalBackupCount => Jobs.Sum(j => j.BackupCount);
}

/// <summary>
/// Ответ сервера на отчёт агента. Содержит ТОЛЬКО подтверждение приёма —
/// сервер не передаёт агенту никаких команд на выполнение (одностороннее, исходящее соединение).
/// </summary>
public class ReportAckDto
{
    public bool Accepted { get; set; }
    public string? Message { get; set; }

    /// <summary>Имя клиента и сервера, как их видит сервер (для отображения в агенте).</summary>
    public string? ClientName { get; set; }
    public string? ServerName { get; set; }
}
