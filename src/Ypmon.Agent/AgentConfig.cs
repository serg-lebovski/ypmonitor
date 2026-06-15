namespace Ypmon.Agent;

/// <summary>Конфигурация агента (хранится в config.json рядом с исполняемым файлом).</summary>
public class AgentConfig
{
    /// <summary>Адрес сервера YPMon, например http://10.0.0.1:8080</summary>
    public string ServerUrl { get; set; } = "";

    /// <summary>API-ключ сервера клиента (выдаётся в веб-интерфейсе при добавлении сервера).</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>Как часто отправлять отчёты на сервер, секунд.</summary>
    public int ReportIntervalSeconds { get; set; } = 60;

    /// <summary>Путь к pg_dump. Пусто = автопоиск (PATH и стандартные папки PostgreSQL).</summary>
    public string PgDumpPath { get; set; } = "";

    /// <summary>Имя службы Windows (для установки и самообновления).</summary>
    public string ServiceName { get; set; } = "YpmonAgent";

    /// <summary>Автоматически проверять и устанавливать обновления агента с сервера (раз в день).</summary>
    public bool AutoUpdate { get; set; } = true;

    /// <summary>Когда в последний раз проверяли обновление (служебное поле).</summary>
    public DateTimeOffset? LastUpdateCheck { get; set; }

    /// <summary>Задания резервного копирования PostgreSQL.</summary>
    public List<PostgresBackupJob> PostgresJobs { get; set; } = new();

    /// <summary>Задания архивации файлов.</summary>
    public List<FileArchiveJob> FileArchiveJobs { get; set; } = new();

    /// <summary>Настройки просмотра логов архивации MSSQL.</summary>
    public MssqlLogConfig Mssql { get; set; } = new();
}

public class PostgresBackupJob
{
    public string Name { get; set; } = "Postgres";
    public bool Enabled { get; set; } = true;

    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 5432;
    public string Username { get; set; } = "postgres";
    public string Password { get; set; } = "";
    public string Database { get; set; } = "";

    /// <summary>Куда складывать дампы.</summary>
    public string BackupDir { get; set; } = "";

    /// <summary>Сколько последних копий хранить (0 = не удалять).</summary>
    public int RetentionCount { get; set; } = 7;

    /// <summary>Запускать каждые N минут.</summary>
    public int IntervalMinutes { get; set; } = 1440;

    /// <summary>Дополнительные аргументы pg_dump (напр. --format=custom).</summary>
    public string ExtraArgs { get; set; } = "--format=custom";
}

public class FileArchiveJob
{
    public string Name { get; set; } = "Файлы";
    public bool Enabled { get; set; } = true;

    /// <summary>Папки/файлы для архивации (можно указывать сетевые пути \\сервер\папка).</summary>
    public List<string> SourcePaths { get; set; } = new();

    /// <summary>Куда складывать zip-архивы (можно сетевой путь \\сервер\папка).</summary>
    public string ArchiveDir { get; set; } = "";

    public int RetentionCount { get; set; } = 7;
    public int IntervalMinutes { get; set; } = 1440;

    /// <summary>
    /// Учётная запись Windows для доступа к сетевым папкам (источникам и/или папке архивов).
    /// Формат логина: DOMAIN\User или СЕРВЕР\User. Пусто — текущая учётная запись службы.
    /// </summary>
    public string NetworkUsername { get; set; } = "";
    public string NetworkPassword { get; set; } = "";
}

public class MssqlLogConfig
{
    public bool Enabled { get; set; }

    /// <summary>Папка, где MSSQL/средство бэкапа складывает логи.</summary>
    public string LogFolder { get; set; } = "";

    /// <summary>Маска файлов логов.</summary>
    public string FilePattern { get; set; } = "*.txt";
}
