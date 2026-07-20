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

/// <summary>Состояние физического накопителя (SMART/WMI). Необязательное — старые агенты его не присылают.</summary>
public class PhysicalDiskDto
{
    /// <summary>Модель/имя накопителя (FriendlyName).</summary>
    public string Name { get; set; } = "";

    /// <summary>Серийный номер (если доступен).</summary>
    public string? Serial { get; set; }

    /// <summary>Здоровье: Healthy / Warning / Unhealthy / Unknown.</summary>
    public string Health { get; set; } = "Unknown";

    /// <summary>Температура, °C (если доступна).</summary>
    public int? TemperatureC { get; set; }

    /// <summary>Объём накопителя, байт.</summary>
    public long SizeBytes { get; set; }
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

    /// <summary>Здоровье физических накопителей (SMART/WMI). Присылается в полном отчёте; может быть пустым
    /// (старый агент, не-Windows, нет данных WMI). Сервер обрабатывает опционально.</summary>
    public List<PhysicalDiskDto> PhysicalDisks { get; set; } = new();

    /// <summary>
    /// Температура процессора, °C. null — датчик недоступен (нет поддержки WMI на этой машине,
    /// виртуалка, нет прав). Передаётся и в heartbeat: значение дешёвое, а перегрев надо ловить быстро.
    /// Старые агенты поле не присылают — сервер обрабатывает его опционально.
    /// </summary>
    public double? CpuTemperatureC { get; set; }

    /// <summary>
    /// true — отчёт отправлен внепланово, потому что агент сам заметил перегрев процессора.
    /// Сервер по нему понимает, что это срочный отчёт, а не очередной по расписанию.
    /// </summary>
    public bool CpuOverheatTrigger { get; set; }

    /// <summary>Интервал heartbeat агента, сек. Сервер по нему понимает, когда агент реально «пропал»
    /// (порог офлайна = 2×интервал + запас), а не ждёт фиксированный глобальный порог.</summary>
    public int HeartbeatIntervalSeconds { get; set; }

    /// <summary>Интервал полного отчёта агента, сек (информационно, показывается в карточке сервера).</summary>
    public int ReportIntervalSeconds { get; set; }

    /// <summary>
    /// Номер последней выполненной агентом команды. По нему сервер понимает, что команда
    /// дошла и исполнена, и перестаёт её выдавать. 0 — агент команд не выполнял (или он старой версии).
    /// </summary>
    public long LastCommandId { get; set; }
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
    // Команды сервера агенту.
    //
    // Агент НЕ слушает порты и не принимает входящих соединений: он сам, отвечая
    // на свой же heartbeat, видит номер команды и выполняет её. Список команд
    // фиксирован и зашит в агенте — с сервера приходит только НОМЕР команды.
    // Ни путей, ни аргументов, ни строк для исполнения сервер не передаёт никогда.
    // ======================================================================

    /// <summary>
    /// Номер команды: уникальный, одноразовый. Агент запоминает последний выполненный номер
    /// (в файле, переживает перезагрузку) и повторно ту же команду не исполняет — иначе
    /// перехваченный ответ можно было бы проиграть заново, а перезагрузка ушла бы в цикл.
    /// 0 — команды нет.
    /// </summary>
    public long CommandId { get; set; }

    /// <summary>Какая именно команда (из закрытого списка <see cref="AgentCommand"/>).</summary>
    public AgentCommand Command { get; set; } = AgentCommand.None;

    /// <summary>Когда команда выдана — агент сам отбрасывает просроченные (см. <see cref="CommandTtlMinutes"/>).</summary>
    public DateTimeOffset? CommandIssuedAt { get; set; }

    /// <summary>Сколько минут команда считается актуальной. Офлайн-агент, вернувшись через сутки, её не выполнит.</summary>
    public const int CommandTtlMinutes = 10;

    // --- Совместимость с агентами до 2.7 ---
    // Старые агенты не знают про CommandId и читают только эти флаги. Сервер выставляет
    // и то, и другое, пока в парке остаются старые версии. Команды «перезагрузить» у старых
    // агентов нет — они её просто не увидят.

    /// <summary>Устарело (агенты до 2.7): сервер просит прислать полный отчёт сейчас.</summary>
    public bool ReportRequested { get; set; }

    /// <summary>Устарело (агенты до 2.7): сервер просит немедленно проверить и установить обновление.</summary>
    public bool UpdateRequested { get; set; }
}

/// <summary>
/// Закрытый список команд, которые агент умеет выполнять. Расширять его можно только
/// изменением кода агента — с сервера приходит лишь номер из этого перечисления.
/// </summary>
public enum AgentCommand
{
    None = 0,
    /// <summary>Прислать полный отчёт сейчас, не дожидаясь расписания.</summary>
    RequestReport = 1,
    /// <summary>Проверить обновление на сервере и установить, если оно новее.</summary>
    UpdateAgent = 2,
    /// <summary>Перезагрузить машину (с отсрочкой, чтобы агент успел подтвердить приём).</summary>
    RebootMachine = 3,
}
