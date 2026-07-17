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

    /// <summary>Физический адрес (где стоит). Используется для точки на Яндекс.Карте дашборда.</summary>
    public string? PhysicalAddress { get; set; }

    // --- Кэш геокодирования адреса для карты (чтобы не геокодировать заново при каждой загрузке) ---

    /// <summary>Широта точки на карте (заполняется геокодером по PhysicalAddress).</summary>
    public double? Latitude { get; set; }

    /// <summary>Долгота точки на карте.</summary>
    public double? Longitude { get; set; }

    /// <summary>Адрес, который был геокодирован (для сброса кэша при смене PhysicalAddress).</summary>
    public string? GeoAddress { get; set; }

    /// <summary>Локальный IP сервера (в сети клиента).</summary>
    public string? IpAddress { get; set; }

    /// <summary>Внешний IP сервера / роутера. Именно его пингует мониторинг доступности интернета.</summary>
    public string? ExternalIpAddress { get; set; }

    public string? Description { get; set; }

    /// <summary>Ключ, которым агент аутентифицируется при отправке отчётов.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>Порог тревоги: нет новых файлов в папках дольше N дней (0 = использовать глобальное значение).</summary>
    public int BackupStaleDays { get; set; } = 0;

    // [YPMON-REMOTE-CMD] Флаги-команды агенту (вырезать после релиза, см. RemoteCommandService).

    /// <summary>[YPMON-REMOTE-CMD] Администратор запросил у агента полный отчёт (агент заберёт при следующей связи).</summary>
    public bool ReportRequested { get; set; }

    /// <summary>[YPMON-REMOTE-CMD] Администратор запросил принудительное обновление агента (одноразовый флаг).</summary>
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

    /// <summary>Объёмы до/после падения (для показа) и когда обнаружено.</summary>
    public long BackupShrinkFromBytes { get; set; }
    public long BackupShrinkToBytes { get; set; }
    public DateTimeOffset? BackupShrinkAt { get; set; }

    public List<Report> Reports { get; set; } = new();

    /// <summary>
    /// Сервер «офлайн», если давно не было отчётов. Если агент сообщил свой интервал heartbeat,
    /// порог = 2×интервал + 30 сек (но не меньше 90) — офлайн ловится «сразу», с поправкой на
    /// настроенную агентом частоту; иначе — глобальный порог из настроек.
    /// </summary>
    public bool IsOffline(int offlineThresholdSeconds)
    {
        if (LastSeenAt is null) return true;
        var threshold = LastHeartbeatIntervalSeconds > 0
            ? Math.Max(90, LastHeartbeatIntervalSeconds * 2 + 30)
            : offlineThresholdSeconds;
        return (DateTimeOffset.UtcNow - LastSeenAt.Value).TotalSeconds > threshold;
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

    /// <summary>API-ключ Яндекс.Карт (JavaScript API) для карты серверов на дашборде. Пусто — карта не показывается.</summary>
    public string? YandexMapsApiKey { get; set; }
}
