namespace Ypmon.Agent;

/// <summary>Конфигурация агента (config.json рядом с исполняемым файлом).</summary>
public class AgentConfig
{
    /// <summary>Адрес сервера YPMon, например http://10.0.0.1:8081</summary>
    public string ServerUrl { get; set; } = "";

    /// <summary>API-ключ сервера клиента (из веб-интерфейса сервера).</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>Интервал полного отчёта (папки, ошибки журнала), секунд.</summary>
    public int ReportIntervalSeconds { get; set; } = 300;

    /// <summary>Интервал лёгкого отчёта о связи (heartbeat), секунд.</summary>
    public int HeartbeatIntervalSeconds { get; set; } = 60;

    /// <summary>Имя службы Windows.</summary>
    public string ServiceName { get; set; } = "YpmonAgent";

    /// <summary>Автоматически проверять и ставить обновления с сервера (раз в день).</summary>
    public bool AutoUpdate { get; set; } = true;

    /// <summary>Служебное поле: когда последний раз проверяли обновление.</summary>
    public DateTimeOffset? LastUpdateCheck { get; set; }

    /// <summary>Наблюдаемые папки с бэкапами.</summary>
    public List<MonitoredFolder> Folders { get; set; } = new();

    /// <summary>Чтение журнала событий Windows.</summary>
    public EventLogConfig EventLog { get; set; } = new();
}

/// <summary>
/// Папка с бэкапами. Агент только читает: количество файлов, объём и дату самого свежего
/// (любого формата). Порог устаревания и алерт настраиваются на сервере.
/// </summary>
public class MonitoredFolder
{
    public string Name { get; set; } = "Бэкапы";
    public bool Enabled { get; set; } = true;

    /// <summary>Путь к папке (можно сетевой \\сервер\папка).</summary>
    public string Path { get; set; } = "";

    /// <summary>Учётная запись Windows для сетевой папки (DOMAIN\User; пусто — текущая).</summary>
    public string NetworkUsername { get; set; } = "";
    public string NetworkPassword { get; set; } = "";
}

/// <summary>Чтение журнала событий Windows (только чтение).</summary>
public class EventLogConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>Какие журналы читать (System, Application и т.д.).</summary>
    public List<string> Logs { get; set; } = new() { "System", "Application" };

    /// <summary>Передавать не только ошибки, но и предупреждения.</summary>
    public bool IncludeWarnings { get; set; } = false;

    /// <summary>Максимум записей за отчёт на каждый журнал.</summary>
    public int MaxEntriesPerReport { get; set; } = 50;

    /// <summary>При первом запуске — насколько назад смотреть, часов.</summary>
    public int LookbackHoursOnFirstRun { get; set; } = 24;
}
