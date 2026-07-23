using Ypmon.Shared;

namespace Ypmon.Server.Data;

/// <summary>Роль пользователя веб-интерфейса.</summary>
public enum UserRole
{
    /// <summary>Мониторинг — только просмотр.</summary>
    Viewer = 0,
    /// <summary>Администратор — полный доступ, включая настройки сервера и пользователей.</summary>
    Admin = 1,
    /// <summary>Инженер — просмотр, добавление и редактирование клиентов/серверов. Без настроек сервера.</summary>
    Engineer = 2
}

/// <summary>Учётная запись для входа в веб-интерфейс.</summary>
public class AppUser
{
    public int Id { get; set; }
    public string Username { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string PasswordSalt { get; set; } = "";
    public UserRole Role { get; set; } = UserRole.Admin;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastLoginAt { get; set; }
}

/// <summary>Клиент — группа серверов.</summary>
public class Client
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>ID темы (forum topic) в Telegram-группе, куда пишется ежедневный отчёт по этому клиенту.</summary>
    public long? TelegramTopicId { get; set; }

    // --- Роутер клиента: у некоторых клиентов нет серверов, но пинг до роутера знать полезно ---

    /// <summary>Адрес роутера клиента (IP/hostname). Заполнен — сервер пингует его по расписанию.</summary>
    public string? RouterAddress { get; set; }

    /// <summary>Как часто пинговать роутер, сек (по умолчанию 60).</summary>
    public int RouterPingIntervalSeconds { get; set; } = 60;

    // --- Дополнительные адресаты отчёта об архивации (по серверам этого клиента) ---

    /// <summary>Дополнительный e-mail клиента, куда дублируется отчёт об архивации.</summary>
    public string? ReportEmail { get; set; }

    /// <summary>Telegram chat_id пользователя для личных сообщений от бота (нужно, чтобы он написал боту /start).</summary>
    public string? ReportTelegramChatId { get; set; }

    /// <summary>Текущая тревога «роутер не отвечает» (для уведомлений по переходам).</summary>
    public bool AlertRouterPingActive { get; set; }

    /// <summary>Итог последней проверки роутера (null — ещё не проверялся).</summary>
    public bool? LastRouterPingOk { get; set; }
    public DateTimeOffset? LastRouterPingAt { get; set; }

    public List<MonitoredServer> Servers { get; set; } = new();
}

/// <summary>Наблюдаемый сервер клиента (к нему подключается агент).</summary>
public class MonitoredServer
{
    public int Id { get; set; }
    public int ClientId { get; set; }
    public Client? Client { get; set; }

    public string Name { get; set; } = "";

    /// <summary>Физический адрес (где стоит).</summary>
    public string? PhysicalAddress { get; set; }

    /// <summary>Локальный IP сервера (в сети клиента).</summary>
    public string? IpAddress { get; set; }

    /// <summary>Внешний IP сервера / роутера. Именно его пингует мониторинг доступности интернета.</summary>
    public string? ExternalIpAddress { get; set; }

    public string? Description { get; set; }

    /// <summary>Ключ, которым агент аутентифицируется при отправке отчётов.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>Порог тревоги: нет новых файлов в папках дольше N дней (0 = использовать глобальное значение).
    /// Это порог по умолчанию для всего сервера; для отдельных папок можно переопределить (см. FolderStaleDaysJson).</summary>
    public int BackupStaleDays { get; set; } = 0;

    /// <summary>
    /// Пороги устаревания по конкретным папкам (JSON {"имя папки": дней}). Позволяет держать на одном
    /// сервере папки с разной частотой бэкапа (ежедневная, еженедельная). 0/нет значения → берётся
    /// BackupStaleDays, иначе глобальный DefaultBackupStaleDays. Задаётся на сервере, агент не меняем.
    /// </summary>
    public string? FolderStaleDaysJson { get; set; }

    // --- Команды агенту (см. RemoteCommandService) ---
    // Агент забирает команду сам, отвечая на heartbeat; входящих соединений к нему нет.

    /// <summary>Ожидающая команда из закрытого списка (0 — нет). Хранится числом = <see cref="AgentCommand"/>.</summary>
    public int PendingCommand { get; set; }

    /// <summary>Уникальный номер ожидающей команды — защита от повторного исполнения.</summary>
    public long PendingCommandId { get; set; }

    /// <summary>Когда команда выдана (для срока годности).</summary>
    public DateTimeOffset? PendingCommandAt { get; set; }

    /// <summary>Кто выдал команду (логин) — для журнала и карточки сервера.</summary>
    public string? PendingCommandBy { get; set; }

    /// <summary>Номер последней команды, которую агент подтвердил выполненной.</summary>
    public long LastExecutedCommandId { get; set; }

    /// <summary>Когда агент подтвердил выполнение последней команды.</summary>
    public DateTimeOffset? LastExecutedCommandAt { get; set; }

    /// <summary>Устарело (агенты до 2.7): флаг «нужен полный отчёт».</summary>
    public bool ReportRequested { get; set; }

    /// <summary>Устарело (агенты до 2.7): флаг «обновись принудительно».</summary>
    public bool UpdateRequested { get; set; }

    // --- Мониторинг доступности и дисков (настраивается в карточке сервера) ---

    /// <summary>Уведомлять в Telegram, когда агент перестаёт выходить на связь (пропала связь с сервером).</summary>
    public bool MonitorOfflineAlert { get; set; }

    /// <summary>Пинговать IP-адрес сервера: если ping пропал — на адресе пропал интернет.</summary>
    public bool MonitorPing { get; set; }

    /// <summary>Как часто пинговать IP, сек (по умолчанию 60).</summary>
    public int PingIntervalSeconds { get; set; } = 60;

    /// <summary>Пороги по дискам: JSON-словарь {"C:": 10} — минимальный % свободного места (0/нет = выкл).</summary>
    public string? DiskAlertsJson { get; set; }

    /// <summary>Последние данные о дисках от агента (JSON List&lt;DiskStatusDto&gt;), обновляются и heartbeat'ом.</summary>
    public string? LastDisksJson { get; set; }

    /// <summary>Последнее здоровье физических накопителей (JSON List&lt;PhysicalDiskDto&gt;), из полного отчёта.</summary>
    public string? LastPhysicalDisksJson { get; set; }

    /// <summary>Активна тревога «проблема со здоровьем диска (SMART)» — есть хотя бы один Warning/Unhealthy.</summary>
    public bool AlertDiskHealthActive { get; set; }

    /// <summary>Последнее известное здоровье каждого накопителя (JSON serial/name → Health) — для детекта
    /// ухудшения показателей SMART (уведомляем, когда становится хуже).</summary>
    public string? DiskHealthStateJson { get; set; }

    /// <summary>Накопители, по которым температура уже превысила порог (JSON-список ключей) — для тревог по переходам.</summary>
    public string? DiskTempAlertJson { get; set; }

    /// <summary>Последняя температура процессора, °C (от агента). null — датчик недоступен или агент старый.</summary>
    public double? LastCpuTemperatureC { get; set; }

    /// <summary>Время последней загрузки машины (от агента 2.8+). По нему считается аптайм в отчёте.</summary>
    public DateTimeOffset? LastBootAt { get; set; }

    // --- Тихий режим (см. AlertSuppression) ---

    /// <summary>Ручная заглушка тревог по этому серверу до указанного момента (UTC). null/прошлое — не заглушено.</summary>
    public DateTimeOffset? AlertSnoozeUntil { get; set; }

    /// <summary>Кто и зачем заглушил — для подсказки в интерфейсе.</summary>
    public string? AlertSnoozeReason { get; set; }

    /// <summary>Окна обслуживания (JSON List&lt;MaintWindow&gt;): в это время не тревожим по офлайну/ping.</summary>
    public string? MaintenanceWindowsJson { get; set; }

    /// <summary>Активна тревога «перегрев процессора» — чтобы уведомлять по переходам, а не каждый отчёт.</summary>
    public bool AlertCpuOverheatActive { get; set; }

    // Текущие состояния тревог — чтобы уведомлять по переходам, а не спамить каждую минуту.
    public bool AlertOfflineActive { get; set; }
    public bool AlertPingActive { get; set; }
    /// <summary>Диски, по которым тревога уже отправлена (JSON-массив имён).</summary>
    public string? AlertDisksActiveJson { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    // --- Кэш последнего состояния (для быстрого дашборда) ---
    public DateTimeOffset? LastSeenAt { get; set; }
    public DateTimeOffset? LastReportedAt { get; set; }
    public JobOutcome LastOutcome { get; set; } = JobOutcome.Unknown;
    public bool LastServerAvailable { get; set; }
    public int LastBackupCount { get; set; }
    public string? LastMachineName { get; set; }
    public string? LastAgentVersion { get; set; }

    /// <summary>Интервал heartbeat агента, сек (0 — агент ещё не сообщал). По нему считается порог «офлайн».</summary>
    public int LastHeartbeatIntervalSeconds { get; set; }

    /// <summary>Интервал полного отчёта агента, сек (информационно).</summary>
    public int LastReportIntervalSeconds { get; set; }

    /// <summary>Полный последний отчёт (JSON AgentReportDto).</summary>
    public string? LastReportJson { get; set; }

    // --- Контроль «усыхания» бэкапа: объём резко упал → возможно, что-то не так ---

    /// <summary>Опорный объём бэкапов (для сравнения со следующим отчётом), байт.</summary>
    public long PrevBackupSizeBytes { get; set; }

    /// <summary>Активна тревога «объём бэкапа резко уменьшился» (ждёт подтверждения администратора).</summary>
    public bool BackupShrinkActive { get; set; }

    /// <summary>Размеры предыдущего/нового файла бэкапа (для показа) и когда обнаружено.</summary>
    public long BackupShrinkFromBytes { get; set; }
    public long BackupShrinkToBytes { get; set; }
    public DateTimeOffset? BackupShrinkAt { get; set; }

    /// <summary>Папка, в которой сработала тревога усадки (для показа в баннере/уведомлении).</summary>
    public string? BackupShrinkFolder { get; set; }

    /// <summary>
    /// Последний увиденный самый свежий файл в каждой папке (JSON {"папка": {"Name","Size"}}).
    /// По нему сравниваем НОВЫЙ файл с ПРЕДЫДУЩИМ, а не суммарный объём всей папки.
    /// </summary>
    public string? LastBackupFilesJson { get; set; }

    public List<Report> Reports { get; set; } = new();

    /// <summary>
    /// Сервер «офлайн», если давно не было отчётов. Если агент сообщил свой интервал heartbeat,
    /// порог = 2×интервал + 30 сек (но не меньше 90) — офлайн ловится «сразу», с поправкой на
    /// настроенную агентом частоту; иначе — глобальный порог из настроек.
    /// </summary>
    public bool IsOffline(int offlineThresholdSeconds) => IsOffline(offlineThresholdSeconds, DateTimeOffset.UtcNow);

    /// <summary>
    /// То же, но относительно заданного момента. Нужно там, где данные читаются один раз,
    /// а используются потом долго (рассылка отчёта идёт минутами): иначе сервер, прочитанный
    /// в начале рассылки, к её концу «уходит в офлайн» просто из-за хода часов.
    /// </summary>
    public bool IsOffline(int offlineThresholdSeconds, DateTimeOffset now)
    {
        if (LastSeenAt is null) return true;
        var threshold = LastHeartbeatIntervalSeconds > 0
            ? Math.Max(90, LastHeartbeatIntervalSeconds * 2 + 30)
            : offlineThresholdSeconds;
        return (now - LastSeenAt.Value).TotalSeconds > threshold;
    }
}

/// <summary>Исторический отчёт от агента.</summary>
public class Report
{
    public long Id { get; set; }
    public int ServerId { get; set; }
    public MonitoredServer? Server { get; set; }

    public DateTimeOffset ReceivedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ReportedAt { get; set; }
    public bool ServerAvailable { get; set; }
    public JobOutcome Outcome { get; set; }
    public int BackupCount { get; set; }
    public string? MachineName { get; set; }

    /// <summary>Полный JSON отчёта.</summary>
    public string PayloadJson { get; set; } = "";
}

/// <summary>Ошибка/предупреждение из журнала событий Windows, присланная агентом.</summary>
public class AgentEvent
{
    public long Id { get; set; }
    public int ServerId { get; set; }
    public MonitoredServer? Server { get; set; }

    public DateTimeOffset ReceivedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset TimeCreated { get; set; }
    public string LogName { get; set; } = "";
    public string Source { get; set; } = "";
    public string Level { get; set; } = "";
    public int EventId { get; set; }
    public string Message { get; set; } = "";
}

/// <summary>Запись журнала действий администратора (кто, когда, что сделал).</summary>
public class AuditEntry
{
    public long Id { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Логин пользователя, выполнившего действие.</summary>
    public string Username { get; set; } = "";

    /// <summary>Короткое описание действия (напр. «Перевыпущен API-ключ»).</summary>
    public string Action { get; set; } = "";

    /// <summary>Дополнительные детали (объект действия, значения).</summary>
    public string? Details { get; set; }
}

/// <summary>Категория записи в журнале сервера (для фильтра в интерфейсе).</summary>
public enum LogArea
{
    /// <summary>Прочее: запуск, фоновые службы, обновления.</summary>
    System = 0,
    /// <summary>Вход/выход пользователей, отказы в доступе.</summary>
    Auth = 1,
    /// <summary>Приём отчётов от агентов.</summary>
    Reports = 2,
    /// <summary>Оповещения: Telegram, почта.</summary>
    Notify = 3,
}

/// <summary>
/// Запись журнала сервера: аутентификация, приём отчётов, ошибки.
/// Пишется как явными вызовами <see cref="Ypmon.Server.Services.ServerLogService"/>,
/// так и перехватом предупреждений/ошибок из обычного ILogger.
/// </summary>
public class ServerLog
{
    public long Id { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Information / Warning / Error.</summary>
    public string Level { get; set; } = "Information";

    public LogArea Area { get; set; } = LogArea.System;

    /// <summary>Текст события.</summary>
    public string Message { get; set; } = "";

    /// <summary>Кто (логин пользователя или имя сервера/агента), если применимо.</summary>
    public string? Who { get; set; }

    /// <summary>IP-адрес источника запроса, если применимо.</summary>
    public string? Ip { get; set; }

    /// <summary>Подробности: текст исключения, детали запроса.</summary>
    public string? Details { get; set; }
}

/// <summary>
/// Загрузка/выключение машины клиента. Накапливается из полных отчётов агента 2.8+:
/// агент присылает окно за последние недели, сервер добавляет только новые записи.
/// Нужна для отчёта клиенту: сколько раз сервер перезагружался и были ли аварийные выключения.
/// </summary>
public class RebootEvent
{
    public long Id { get; set; }
    public int ServerId { get; set; }
    public MonitoredServer? Server { get; set; }

    public DateTimeOffset At { get; set; }

    /// <summary>Startup / Shutdown / Unexpected / Initiated.</summary>
    public string Kind { get; set; } = "";

    /// <summary>Кто инициировал и почему (если Windows сообщила).</summary>
    public string? Reason { get; set; }
}

/// <summary>Глобальные настройки сервера (одна запись, key/value-подход не нужен).</summary>
public class ServerSettings
{
    public int Id { get; set; }

    /// <summary>Через сколько секунд молчания агент считается офлайн.</summary>
    public int OfflineThresholdSeconds { get; set; } = 300;

    /// <summary>Сколько дней хранить историю отчётов.</summary>
    public int ReportRetentionDays { get; set; } = 30;

    /// <summary>Порог по умолчанию: тревога, если в папках нет новых файлов дольше N дней.</summary>
    public int DefaultBackupStaleDays { get; set; } = 1;

    /// <summary>
    /// Игнорируемые ошибки Windows: по одному правилу в строке — либо код события (число),
    /// либо имя/подстрока источника. Совпавшие события не сохраняются и не участвуют в алертах.
    /// </summary>
    public string? WindowsErrorIgnore { get; set; }

    // --- Оповещения ---
    public bool AlertsEnabled { get; set; }

    public bool TelegramEnabled { get; set; }
    public string? TelegramBotToken { get; set; }

    /// <summary>ID группы Telegram (супергруппа с включёнными темами/Topics), напр. -1001234567890.</summary>
    public string? TelegramChatId { get; set; }

    /// <summary>Прокси для доступа к Telegram API (http://host:port или socks5://host:port). Пусто = без прокси.</summary>
    public string? TelegramProxyUrl { get; set; }

    /// <summary>Публичный адрес сервера для ссылок в сообщениях (напр. http://10.10.20.25:8080).</summary>
    public string? PublicBaseUrl { get; set; }

    /// <summary>Слать ежедневный отчёт по архивации в темы клиентов.</summary>
    public bool DailyReportEnabled { get; set; }

    /// <summary>Час ежедневного отчёта по омскому времени (UTC+6). По умолчанию 10.</summary>
    public int DailyReportHourOmsk { get; set; } = 10;

    /// <summary>
    /// Дата (по Омску, «гггг-мм-дд») последней автоматической рассылки. Хранится в БД, а не в памяти:
    /// перезапуск сервера в час отчёта иначе рассылал его всем клиентам повторно.
    /// </summary>
    public string? LastDailyReportDate { get; set; }

    // --- WireGuard для доступа бота к Telegram (userspace wireproxy → SOCKS5) ---
    public bool WireGuardEnabled { get; set; }

    /// <summary>Содержимое конфигурации WireGuard (.conf), которую загрузил администратор.</summary>
    public string? WireGuardConfig { get; set; }

    public bool EmailEnabled { get; set; }
    public string? SmtpHost { get; set; }
    public int SmtpPort { get; set; } = 587;
    public bool SmtpUseSsl { get; set; } = true;
    public string? SmtpUser { get; set; }
    public string? SmtpPassword { get; set; }
    public string? EmailFrom { get; set; }
    public string? EmailTo { get; set; }

    /// <summary>Основной адрес для отчётов об архивации (куда дублируется отчёт по всем клиентам).</summary>
    public string? ArchiveReportEmailTo { get; set; }

    // --- Пороги мониторинга здоровья дисков (SMART) ---

    /// <summary>Слать уведомления по SMART (ухудшение здоровья, перегрев).</summary>
    public bool SmartAlertsEnabled { get; set; } = true;

    /// <summary>Тревожить и на статус Warning (иначе только на Unhealthy).</summary>
    public bool SmartAlertOnWarning { get; set; } = true;

    /// <summary>Порог температуры накопителя, °C (тревога при превышении). 0 = не следить за температурой.</summary>
    public int SmartTempThresholdC { get; set; } = 55;

    // --- Перегрев процессора ---

    /// <summary>Слать уведомления о перегреве процессора.</summary>
    public bool CpuTempAlertsEnabled { get; set; } = true;

    /// <summary>
    /// Порог температуры процессора, °C. 0 = не следить.
    /// Свой порог есть и у агента (когда слать внеплановый отчёт) — по умолчанию оба равны 90.
    /// </summary>
    public int CpuTempThresholdC { get; set; } = 90;

    // --- Внешний адрес сервера (под которым к нему подключаются удалённые агенты) ---

    /// <summary>Внешний IP/хост, под которым доступен сервер снаружи (для удалённых агентов). Пусто — только локальный адрес.</summary>
    public string? ExternalAddress { get; set; }

    /// <summary>Внешний порт приёма отчётов (если отличается от порта приёма по умолчанию 8081).</summary>
    public int ExternalPort { get; set; } = 8081;
}
