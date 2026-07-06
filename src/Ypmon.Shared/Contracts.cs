namespace Ypmon.Shared;

/// <summary>Итог проверки (папки архивации, общий статус).</summary>
public enum JobOutcome
{
    Unknown = 0,
    Ok = 1,
    Warning = 2,
    Error = 3
}

/// <summary>
/// Статус одной наблюдаемой папки с бэкапами. Агент присылает фактические данные
/// (количество файлов, дата последнего, объём, доступность). Порог «нет новых файлов N дней»
/// и итоговый статус вычисляет сервер.
/// </summary>
public class FolderStatusDto
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";

    /// <summary>Доступна ли папка (существует и читается).</summary>
    public bool Accessible { get; set; }

    /// <summary>Сколько файлов сейчас в папке (любого формата).</summary>
    public int FileCount { get; set; }

    /// <summary>Суммарный объём файлов, байт.</summary>
    public long TotalSizeBytes { get; set; }

    /// <summary>Дата/время самого свежего файла.</summary>
    public DateTimeOffset? LastBackupAt { get; set; }

    /// <summary>Имя самого свежего файла (для наглядности).</summary>
    public string? LastFileName { get; set; }

    /// <summary>Примечание агента (например, ошибка доступа).</summary>
    public string? Message { get; set; }

    /// <summary>Итог, вычисленный сервером (агент оставляет Unknown).</summary>
    public JobOutcome Outcome { get; set; } = JobOutcome.Unknown;
}

/// <summary>Ошибка/предупреждение из журнала событий Windows.</summary>
public class EventLogEntryDto
{
    public string LogName { get; set; } = "";
    public string Source { get; set; } = "";
    public string Level { get; set; } = "";
    public int EventId { get; set; }
    public DateTimeOffset TimeCreated { get; set; }
    public string Message { get; set; } = "";
}

/// <summary>
/// Отчёт агента серверу (POST /api/report). Идентификация — по заголовку X-Api-Key.
/// Агент только отдаёт данные: статусы папок и ошибки журнала Windows.
/// </summary>
public class AgentReportDto
{
    public string MachineName { get; set; } = "";
    public string AgentVersion { get; set; } = "";
    public DateTimeOffset ReportedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>true — лёгкий отчёт о связи (heartbeat): обновляется только время связи.</summary>
    public bool IsHeartbeat { get; set; }

    /// <summary>Статусы наблюдаемых папок бэкапов.</summary>
    public List<FolderStatusDto> Folders { get; set; } = new();

    /// <summary>Новые ошибки/предупреждения журнала Windows с прошлого отчёта.</summary>
    public List<EventLogEntryDto> EventLogErrors { get; set; } = new();
}

/// <summary>
/// Ответ сервера на отчёт. Только подтверждение приёма — сервер не передаёт агенту команд.
/// </summary>
public class ReportAckDto
{
    public bool Accepted { get; set; }
    public string? Message { get; set; }
    public string? ClientName { get; set; }
    public string? ServerName { get; set; }
}
