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

    /// <summary>Размер самого свежего файла (объём последнего архива), байт.</summary>
    public long LastFileSizeBytes { get; set; }

    /// <summary>Примечание агента (например, ошибка доступа).</summary>
    public string? Message { get; set; }

    /// <summary>Итог, вычисленный сервером (агент оставляет Unknown).</summary>
    public JobOutcome Outcome { get; set; } = JobOutcome.Unknown;
}

/// <summary>Состояние локального диска на машине агента (только чтение DriveInfo).</summary>
public class DiskStatusDto
{
    /// <summary>Буква диска, например "C:".</summary>
    public string Name { get; set; } = "";

    /// <summary>Метка тома (если задана).</summary>
    public string? Label { get; set; }

    public long TotalBytes { get; set; }
    public long FreeBytes { get; set; }

    /// <summary>Процент свободного места (0–100).</summary>
    public double FreePercent => TotalBytes > 0 ? FreeBytes * 100.0 / TotalBytes : 0;
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

    /// <summary>Локальные диски (буква, объём, свободно). Передаются и в heartbeat — данные дешёвые,
    /// а серверные пороги заполненности работают со свежими цифрами.</summary>
    public List<DiskStatusDto> Disks { get; set; } = new();

    /// <summary>Интервал heartbeat агента, сек. Сервер по нему понимает, когда агент реально «пропал»
    /// (порог офлайна = 2×интервал + запас), а не ждёт фиксированный глобальный порог.</summary>
    public int HeartbeatIntervalSeconds { get; set; }

    /// <summary>Интервал полного отчёта агента, сек (информационно, показывается в карточке сервера).</summary>
    public int ReportIntervalSeconds { get; set; }
}

/// <summary>
/// Ответ сервера на отчёт: подтверждение приёма + (временно) флаги-команды, см. блок ниже.
/// </summary>
public class ReportAckDto
{
    public bool Accepted { get; set; }
    public string? Message { get; set; }
    public string? ClientName { get; set; }
    public string? ServerName { get; set; }

    // ======================================================================
    // [YPMON-REMOTE-CMD] Команды сервера агенту — ВРЕЗАТЬ ПОСЛЕ РЕЛИЗА.
    // По замыслу агент строго исходящий и команд не получает. Эти флаги —
    // временное исключение: агент сам вычитывает их в ответе на heartbeat
    // (порты не слушает). Все места употребления помечены [YPMON-REMOTE-CMD].
    // ======================================================================

    /// <summary>[YPMON-REMOTE-CMD] Сервер просит прислать полный отчёт сейчас (кнопка «Запросить отчёт»).</summary>
    public bool ReportRequested { get; set; }

    /// <summary>[YPMON-REMOTE-CMD] Сервер просит немедленно проверить и установить обновление (кнопка «Обновить агента»).</summary>
    public bool UpdateRequested { get; set; }
}
