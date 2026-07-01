using Ypmon.Shared;

namespace Ypmon.Server.Data;

/// <summary>Роль пользователя веб-интерфейса.</summary>
public enum UserRole
{
    Viewer = 0,
    Admin = 1
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

    /// <summary>Сетевой адрес / IP.</summary>
    public string? IpAddress { get; set; }

    public string? Description { get; set; }

    /// <summary>Ключ, которым агент аутентифицируется при отправке отчётов.</summary>
    public string ApiKey { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    // --- Кэш последнего состояния (для быстрого дашборда) ---
    public DateTimeOffset? LastSeenAt { get; set; }
    public DateTimeOffset? LastReportedAt { get; set; }
    public JobOutcome LastOutcome { get; set; } = JobOutcome.Unknown;
    public bool LastServerAvailable { get; set; }
    public int LastBackupCount { get; set; }
    public string? LastMachineName { get; set; }
    public string? LastAgentVersion { get; set; }

    /// <summary>Полный последний отчёт (JSON AgentReportDto).</summary>
    public string? LastReportJson { get; set; }

    public List<Report> Reports { get; set; } = new();

    /// <summary>Сервер «офлайн», если давно не было отчётов.</summary>
    public bool IsOffline(int offlineThresholdSeconds)
        => LastSeenAt is null || (DateTimeOffset.UtcNow - LastSeenAt.Value).TotalSeconds > offlineThresholdSeconds;
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

    // --- Оповещения ---
    public bool AlertsEnabled { get; set; }

    public bool TelegramEnabled { get; set; }
    public string? TelegramBotToken { get; set; }
    public string? TelegramChatId { get; set; }

    /// <summary>Прокси для доступа к Telegram API (http://host:port или socks5://host:port). Пусто = без прокси.</summary>
    public string? TelegramProxyUrl { get; set; }

    public bool EmailEnabled { get; set; }
    public string? SmtpHost { get; set; }
    public int SmtpPort { get; set; } = 587;
    public bool SmtpUseSsl { get; set; } = true;
    public string? SmtpUser { get; set; }
    public string? SmtpPassword { get; set; }
    public string? EmailFrom { get; set; }
    public string? EmailTo { get; set; }
}
