using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Ypmon.Server.Data;
using Ypmon.Server.Services;
using Ypmon.Shared;

var builder = WebApplication.CreateBuilder(args);

// Поддержка запуска как службы Windows и как демона systemd (Linux).
builder.Host.UseWindowsService();
builder.Host.UseSystemd();

// --- Конфигурация ---
// Данные приложения (БД sqlite, логи) держим рядом с исполняемым файлом в подпапке data.
var contentRoot = builder.Environment.ContentRootPath;
var dataDir = Path.Combine(contentRoot, "data");
Directory.CreateDirectory(dataDir);

var dbProvider = (builder.Configuration["Database:Provider"] ?? "sqlite").ToLowerInvariant();
var connString = builder.Configuration["Database:ConnectionString"];
if (string.IsNullOrWhiteSpace(connString) && dbProvider == "sqlite")
    connString = $"Data Source={Path.Combine(dataDir, "ypmon.db")}";

// Порты: веб-интерфейс (8080) и отдельный порт приёма отчётов от агентов (8081).
var httpPort = builder.Configuration.GetValue<int?>("Server:HttpPort") ?? 8080;
var reportsPort = builder.Configuration.GetValue<int?>("Server:ReportsPort") ?? 8081;
// Список разрешённых IP для веб-порта (пусто = разрешено всем).
var allowedIps = IpAllowList.Parse(builder.Configuration["Server:AllowedIps"]);
ServerPorts.WebPort = httpPort;
ServerPorts.ReportsPort = reportsPort;

builder.WebHost.ConfigureKestrel(o =>
{
    o.ListenAnyIP(httpPort);
    if (reportsPort != httpPort) o.ListenAnyIP(reportsPort);
});

// --- Сервисы ---
builder.Services.AddDbContext<AppDbContext>(opt =>
{
    if (dbProvider == "postgres")
        opt.UseNpgsql(connString);
    else
        opt.UseSqlite(connString);
});

// Ключи DataProtection храним в data-томе, чтобы cookie-авторизация и antiforgery-токены
// переживали пересборку контейнера (иначе после каждого обновления — разлогин и ошибки форм).
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDir, "keys")))
    .SetApplicationName("Ypmon");

builder.Services.AddHttpClient();
builder.Services.AddScoped<AlertService>();
builder.Services.AddScoped<AuditService>();
builder.Services.AddSingleton<BackupService>();

// Журнал сервера: очередь в памяти + фоновая запись в БД + перехват предупреждений/ошибок из ILogger.
builder.Services.AddSingleton<ServerLogService>();
builder.Services.AddHostedService<ServerLogWriter>();
builder.Services.AddSingleton<ILoggerProvider>(sp => new DbLoggerProvider(sp.GetRequiredService<ServerLogService>()));
builder.Services.AddScoped<ReportIngestService>();
builder.Services.AddSingleton<ServerUpdateService>();
builder.Services.AddSingleton<WireGuardProxyService>();
builder.Services.AddSingleton<TelegramService>();
builder.Services.AddScoped<TelegramReportService>();
builder.Services.AddScoped<RemoteCommandService>();
builder.Services.AddScoped<ClientReportPdfService>();
builder.Services.AddHostedService<DailyReportScheduler>();
builder.Services.AddHostedService<MaintenanceService>();
builder.Services.AddHostedService<AvailabilityMonitor>();   // пинг IP, офлайн-агенты, пороги дисков

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.LoginPath = "/Login";
        o.LogoutPath = "/Logout";
        o.AccessDeniedPath = "/Login";
        o.ExpireTimeSpan = TimeSpan.FromDays(7);
        o.SlidingExpiration = true;
    });
builder.Services.AddAuthorization();

builder.Services.AddRazorPages(o =>
{
    // Весь UI требует авторизации, кроме страниц логина и первичной настройки.
    o.Conventions.AuthorizeFolder("/");
    o.Conventions.AllowAnonymousToPage("/Login");
    o.Conventions.AllowAnonymousToPage("/Logout");
    o.Conventions.AllowAnonymousToPage("/Setup");
});

var app = builder.Build();

// --- Инициализация БД (с ретраями: внешний Postgres может быть доступен не сразу) ---
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    if (dbProvider == "postgres" && string.IsNullOrWhiteSpace(connString))
        logger.LogWarning("Provider=postgres, но строка подключения пуста. Задайте Database:ConnectionString (env Database__ConnectionString).");

    const int maxAttempts = 12;
    for (var attempt = 1; ; attempt++)
    {
        try
        {
            db.Database.EnsureCreated();

            // Идемпотентное добавление новых столбцов (без EF-миграций).
            try
            {
                if (dbProvider == "postgres")
                {
                    db.Database.ExecuteSqlRaw("ALTER TABLE \"Settings\" ADD COLUMN IF NOT EXISTS \"TelegramProxyUrl\" text;");
                    db.Database.ExecuteSqlRaw("ALTER TABLE \"Settings\" ADD COLUMN IF NOT EXISTS \"DefaultBackupStaleDays\" integer NOT NULL DEFAULT 1;");
                    db.Database.ExecuteSqlRaw("ALTER TABLE \"Settings\" ADD COLUMN IF NOT EXISTS \"WindowsErrorIgnore\" text;");
                    db.Database.ExecuteSqlRaw("ALTER TABLE \"Servers\" ADD COLUMN IF NOT EXISTS \"BackupStaleDays\" integer NOT NULL DEFAULT 0;");
                    db.Database.ExecuteSqlRaw("ALTER TABLE \"Settings\" ADD COLUMN IF NOT EXISTS \"PublicBaseUrl\" text;");
                    db.Database.ExecuteSqlRaw("ALTER TABLE \"Settings\" ADD COLUMN IF NOT EXISTS \"DailyReportEnabled\" boolean NOT NULL DEFAULT false;");
                    db.Database.ExecuteSqlRaw("ALTER TABLE \"Settings\" ADD COLUMN IF NOT EXISTS \"DailyReportHourOmsk\" integer NOT NULL DEFAULT 10;");
                    db.Database.ExecuteSqlRaw("ALTER TABLE \"Settings\" ADD COLUMN IF NOT EXISTS \"WireGuardEnabled\" boolean NOT NULL DEFAULT false;");
                    db.Database.ExecuteSqlRaw("ALTER TABLE \"Settings\" ADD COLUMN IF NOT EXISTS \"WireGuardConfig\" text;");
                    db.Database.ExecuteSqlRaw("ALTER TABLE \"Clients\" ADD COLUMN IF NOT EXISTS \"TelegramTopicId\" bigint;");
                    db.Database.ExecuteSqlRaw("ALTER TABLE \"Servers\" ADD COLUMN IF NOT EXISTS \"ReportRequested\" boolean NOT NULL DEFAULT false;");
                    db.Database.ExecuteSqlRaw("ALTER TABLE \"Servers\" ADD COLUMN IF NOT EXISTS \"UpdateRequested\" boolean NOT NULL DEFAULT false;");
                    db.Database.ExecuteSqlRaw("ALTER TABLE \"Servers\" ADD COLUMN IF NOT EXISTS \"MonitorOfflineAlert\" boolean NOT NULL DEFAULT false;");
                    db.Database.ExecuteSqlRaw("ALTER TABLE \"Servers\" ADD COLUMN IF NOT EXISTS \"MonitorPing\" boolean NOT NULL DEFAULT false;");
                    db.Database.ExecuteSqlRaw("ALTER TABLE \"Servers\" ADD COLUMN IF NOT EXISTS \"DiskAlertsJson\" text;");
                    db.Database.ExecuteSqlRaw("ALTER TABLE \"Servers\" ADD COLUMN IF NOT EXISTS \"LastDisksJson\" text;");
                    db.Database.ExecuteSqlRaw("ALTER TABLE \"Servers\" ADD COLUMN IF NOT EXISTS \"AlertOfflineActive\" boolean NOT NULL DEFAULT false;");
                    db.Database.ExecuteSqlRaw("ALTER TABLE \"Servers\" ADD COLUMN IF NOT EXISTS \"AlertPingActive\" boolean NOT NULL DEFAULT false;");
                    db.Database.ExecuteSqlRaw("ALTER TABLE \"Servers\" ADD COLUMN IF NOT EXISTS \"AlertDisksActiveJson\" text;");
                    db.Database.ExecuteSqlRaw("ALTER TABLE \"Servers\" ADD COLUMN IF NOT EXISTS \"ExternalIpAddress\" text;");
                    db.Database.ExecuteSqlRaw("ALTER TABLE \"Servers\" ADD COLUMN IF NOT EXISTS \"PingIntervalSeconds\" integer NOT NULL DEFAULT 60;");
                    db.Database.ExecuteSqlRaw("ALTER TABLE \"Servers\" ADD COLUMN IF NOT EXISTS \"LastHeartbeatIntervalSeconds\" integer NOT NULL DEFAULT 0;");
                    db.Database.ExecuteSqlRaw("ALTER TABLE \"Servers\" ADD COLUMN IF NOT EXISTS \"LastReportIntervalSeconds\" integer NOT NULL DEFAULT 0;");
                    db.Database.ExecuteSqlRaw("ALTER TABLE \"Servers\" ADD COLUMN IF NOT EXISTS \"PrevBackupSizeBytes\" bigint NOT NULL DEFAULT 0;");
                    db.Database.ExecuteSqlRaw("ALTER TABLE \"Servers\" ADD COLUMN IF NOT EXISTS \"BackupShrinkActive\" boolean NOT NULL DEFAULT false;");
                    db.Database.ExecuteSqlRaw("ALTER TABLE \"Servers\" ADD COLUMN IF NOT EXISTS \"BackupShrinkFromBytes\" bigint NOT NULL DEFAULT 0;");
                    db.Database.ExecuteSqlRaw("ALTER TABLE \"Servers\" ADD COLUMN IF NOT EXISTS \"BackupShrinkToBytes\" bigint NOT NULL DEFAULT 0;");
                    db.Database.ExecuteSqlRaw("ALTER TABLE \"Servers\" ADD COLUMN IF NOT EXISTS \"BackupShrinkAt\" timestamptz;");
                    db.Database.ExecuteSqlRaw("ALTER TABLE \"Clients\" ADD COLUMN IF NOT EXISTS \"RouterAddress\" text;");
                    db.Database.ExecuteSqlRaw("ALTER TABLE \"Clients\" ADD COLUMN IF NOT EXISTS \"RouterPingIntervalSeconds\" integer NOT NULL DEFAULT 60;");
                    db.Database.ExecuteSqlRaw("ALTER TABLE \"Clients\" ADD COLUMN IF NOT EXISTS \"AlertRouterPingActive\" boolean NOT NULL DEFAULT false;");
                    db.Database.ExecuteSqlRaw("ALTER TABLE \"Clients\" ADD COLUMN IF NOT EXISTS \"LastRouterPingOk\" boolean;");
                    db.Database.ExecuteSqlRaw("ALTER TABLE \"Clients\" ADD COLUMN IF NOT EXISTS \"LastRouterPingAt\" timestamptz;");
                    db.Database.ExecuteSqlRaw("ALTER TABLE \"Clients\" ADD COLUMN IF NOT EXISTS \"ReportEmail\" text;");
                    db.Database.ExecuteSqlRaw("ALTER TABLE \"Clients\" ADD COLUMN IF NOT EXISTS \"ReportTelegramChatId\" text;");
                    db.Database.ExecuteSqlRaw("ALTER TABLE \"Settings\" ADD COLUMN IF NOT EXISTS \"ArchiveReportEmailTo\" text;");
                    db.Database.ExecuteSqlRaw("ALTER TABLE \"Settings\" ADD COLUMN IF NOT EXISTS \"ExternalAddress\" text;");
                    db.Database.ExecuteSqlRaw("ALTER TABLE \"Settings\" ADD COLUMN IF NOT EXISTS \"ExternalPort\" integer NOT NULL DEFAULT 8081;");
                    db.Database.ExecuteSqlRaw("ALTER TABLE \"Servers\" ADD COLUMN IF NOT EXISTS \"LastPhysicalDisksJson\" text;");
                    db.Database.ExecuteSqlRaw("ALTER TABLE \"Servers\" ADD COLUMN IF NOT EXISTS \"AlertDiskHealthActive\" boolean NOT NULL DEFAULT false;");
                    db.Database.ExecuteSqlRaw("ALTER TABLE \"Servers\" ADD COLUMN IF NOT EXISTS \"DiskHealthStateJson\" text;");
                    db.Database.ExecuteSqlRaw("ALTER TABLE \"Servers\" ADD COLUMN IF NOT EXISTS \"DiskTempAlertJson\" text;");
                    db.Database.ExecuteSqlRaw("ALTER TABLE \"Settings\" ADD COLUMN IF NOT EXISTS \"SmartAlertsEnabled\" boolean NOT NULL DEFAULT true;");
                    db.Database.ExecuteSqlRaw("ALTER TABLE \"Settings\" ADD COLUMN IF NOT EXISTS \"SmartAlertOnWarning\" boolean NOT NULL DEFAULT true;");
                    db.Database.ExecuteSqlRaw("ALTER TABLE \"Settings\" ADD COLUMN IF NOT EXISTS \"SmartTempThresholdC\" integer NOT NULL DEFAULT 55;");
                    db.Database.ExecuteSqlRaw("ALTER TABLE \"Servers\" ADD COLUMN IF NOT EXISTS \"LastCpuTemperatureC\" double precision;");
                    db.Database.ExecuteSqlRaw("ALTER TABLE \"Servers\" ADD COLUMN IF NOT EXISTS \"AlertCpuOverheatActive\" boolean NOT NULL DEFAULT false;");
                    db.Database.ExecuteSqlRaw("ALTER TABLE \"Settings\" ADD COLUMN IF NOT EXISTS \"CpuTempAlertsEnabled\" boolean NOT NULL DEFAULT true;");
                    db.Database.ExecuteSqlRaw("ALTER TABLE \"Settings\" ADD COLUMN IF NOT EXISTS \"CpuTempThresholdC\" integer NOT NULL DEFAULT 90;");
                    db.Database.ExecuteSqlRaw("ALTER TABLE \"Servers\" ADD COLUMN IF NOT EXISTS \"PendingCommand\" integer NOT NULL DEFAULT 0;");
                    db.Database.ExecuteSqlRaw("ALTER TABLE \"Servers\" ADD COLUMN IF NOT EXISTS \"PendingCommandId\" bigint NOT NULL DEFAULT 0;");
                    db.Database.ExecuteSqlRaw("ALTER TABLE \"Servers\" ADD COLUMN IF NOT EXISTS \"PendingCommandAt\" timestamptz;");
                    db.Database.ExecuteSqlRaw("ALTER TABLE \"Servers\" ADD COLUMN IF NOT EXISTS \"PendingCommandBy\" text;");
                    db.Database.ExecuteSqlRaw("ALTER TABLE \"Servers\" ADD COLUMN IF NOT EXISTS \"LastExecutedCommandId\" bigint NOT NULL DEFAULT 0;");
                    db.Database.ExecuteSqlRaw("ALTER TABLE \"Servers\" ADD COLUMN IF NOT EXISTS \"LastExecutedCommandAt\" timestamptz;");
                    db.Database.ExecuteSqlRaw("ALTER TABLE \"Servers\" ADD COLUMN IF NOT EXISTS \"FolderStaleDaysJson\" text;");
                    db.Database.ExecuteSqlRaw("ALTER TABLE \"Servers\" ADD COLUMN IF NOT EXISTS \"BackupShrinkFolder\" text;");
                    db.Database.ExecuteSqlRaw("ALTER TABLE \"Servers\" ADD COLUMN IF NOT EXISTS \"LastBackupFilesJson\" text;");
                    db.Database.ExecuteSqlRaw("ALTER TABLE \"Settings\" ADD COLUMN IF NOT EXISTS \"LastDailyReportDate\" text;");
                    db.Database.ExecuteSqlRaw("ALTER TABLE \"Servers\" ADD COLUMN IF NOT EXISTS \"LastBootAt\" timestamptz;");
                }
                else
                {
                    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Settings\" ADD COLUMN \"TelegramProxyUrl\" TEXT;"); } catch { }
                    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Settings\" ADD COLUMN \"DefaultBackupStaleDays\" INTEGER NOT NULL DEFAULT 1;"); } catch { }
                    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Settings\" ADD COLUMN \"WindowsErrorIgnore\" TEXT;"); } catch { }
                    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Servers\" ADD COLUMN \"BackupStaleDays\" INTEGER NOT NULL DEFAULT 0;"); } catch { }
                    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Settings\" ADD COLUMN \"PublicBaseUrl\" TEXT;"); } catch { }
                    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Settings\" ADD COLUMN \"DailyReportEnabled\" INTEGER NOT NULL DEFAULT 0;"); } catch { }
                    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Settings\" ADD COLUMN \"DailyReportHourOmsk\" INTEGER NOT NULL DEFAULT 10;"); } catch { }
                    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Settings\" ADD COLUMN \"WireGuardEnabled\" INTEGER NOT NULL DEFAULT 0;"); } catch { }
                    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Settings\" ADD COLUMN \"WireGuardConfig\" TEXT;"); } catch { }
                    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Clients\" ADD COLUMN \"TelegramTopicId\" INTEGER;"); } catch { }
                    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Servers\" ADD COLUMN \"ReportRequested\" INTEGER NOT NULL DEFAULT 0;"); } catch { }
                    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Servers\" ADD COLUMN \"UpdateRequested\" INTEGER NOT NULL DEFAULT 0;"); } catch { }
                    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Servers\" ADD COLUMN \"MonitorOfflineAlert\" INTEGER NOT NULL DEFAULT 0;"); } catch { }
                    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Servers\" ADD COLUMN \"MonitorPing\" INTEGER NOT NULL DEFAULT 0;"); } catch { }
                    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Servers\" ADD COLUMN \"DiskAlertsJson\" TEXT;"); } catch { }
                    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Servers\" ADD COLUMN \"LastDisksJson\" TEXT;"); } catch { }
                    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Servers\" ADD COLUMN \"AlertOfflineActive\" INTEGER NOT NULL DEFAULT 0;"); } catch { }
                    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Servers\" ADD COLUMN \"AlertPingActive\" INTEGER NOT NULL DEFAULT 0;"); } catch { }
                    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Servers\" ADD COLUMN \"AlertDisksActiveJson\" TEXT;"); } catch { }
                    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Servers\" ADD COLUMN \"ExternalIpAddress\" TEXT;"); } catch { }
                    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Servers\" ADD COLUMN \"PingIntervalSeconds\" INTEGER NOT NULL DEFAULT 60;"); } catch { }
                    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Servers\" ADD COLUMN \"LastHeartbeatIntervalSeconds\" INTEGER NOT NULL DEFAULT 0;"); } catch { }
                    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Servers\" ADD COLUMN \"LastReportIntervalSeconds\" INTEGER NOT NULL DEFAULT 0;"); } catch { }
                    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Servers\" ADD COLUMN \"PrevBackupSizeBytes\" INTEGER NOT NULL DEFAULT 0;"); } catch { }
                    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Servers\" ADD COLUMN \"BackupShrinkActive\" INTEGER NOT NULL DEFAULT 0;"); } catch { }
                    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Servers\" ADD COLUMN \"BackupShrinkFromBytes\" INTEGER NOT NULL DEFAULT 0;"); } catch { }
                    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Servers\" ADD COLUMN \"BackupShrinkToBytes\" INTEGER NOT NULL DEFAULT 0;"); } catch { }
                    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Servers\" ADD COLUMN \"BackupShrinkAt\" TEXT;"); } catch { }
                    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Clients\" ADD COLUMN \"RouterAddress\" TEXT;"); } catch { }
                    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Clients\" ADD COLUMN \"RouterPingIntervalSeconds\" INTEGER NOT NULL DEFAULT 60;"); } catch { }
                    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Clients\" ADD COLUMN \"AlertRouterPingActive\" INTEGER NOT NULL DEFAULT 0;"); } catch { }
                    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Clients\" ADD COLUMN \"LastRouterPingOk\" INTEGER;"); } catch { }
                    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Clients\" ADD COLUMN \"LastRouterPingAt\" TEXT;"); } catch { }
                    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Clients\" ADD COLUMN \"ReportEmail\" TEXT;"); } catch { }
                    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Clients\" ADD COLUMN \"ReportTelegramChatId\" TEXT;"); } catch { }
                    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Settings\" ADD COLUMN \"ArchiveReportEmailTo\" TEXT;"); } catch { }
                    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Settings\" ADD COLUMN \"ExternalAddress\" TEXT;"); } catch { }
                    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Settings\" ADD COLUMN \"ExternalPort\" INTEGER NOT NULL DEFAULT 8081;"); } catch { }
                    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Servers\" ADD COLUMN \"LastPhysicalDisksJson\" TEXT;"); } catch { }
                    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Servers\" ADD COLUMN \"AlertDiskHealthActive\" INTEGER NOT NULL DEFAULT 0;"); } catch { }
                    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Servers\" ADD COLUMN \"DiskHealthStateJson\" TEXT;"); } catch { }
                    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Servers\" ADD COLUMN \"DiskTempAlertJson\" TEXT;"); } catch { }
                    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Settings\" ADD COLUMN \"SmartAlertsEnabled\" INTEGER NOT NULL DEFAULT 1;"); } catch { }
                    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Settings\" ADD COLUMN \"SmartAlertOnWarning\" INTEGER NOT NULL DEFAULT 1;"); } catch { }
                    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Settings\" ADD COLUMN \"SmartTempThresholdC\" INTEGER NOT NULL DEFAULT 55;"); } catch { }
                    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Servers\" ADD COLUMN \"LastCpuTemperatureC\" REAL;"); } catch { }
                    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Servers\" ADD COLUMN \"AlertCpuOverheatActive\" INTEGER NOT NULL DEFAULT 0;"); } catch { }
                    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Settings\" ADD COLUMN \"CpuTempAlertsEnabled\" INTEGER NOT NULL DEFAULT 1;"); } catch { }
                    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Settings\" ADD COLUMN \"CpuTempThresholdC\" INTEGER NOT NULL DEFAULT 90;"); } catch { }
                    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Servers\" ADD COLUMN \"PendingCommand\" INTEGER NOT NULL DEFAULT 0;"); } catch { }
                    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Servers\" ADD COLUMN \"PendingCommandId\" INTEGER NOT NULL DEFAULT 0;"); } catch { }
                    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Servers\" ADD COLUMN \"PendingCommandAt\" TEXT;"); } catch { }
                    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Servers\" ADD COLUMN \"PendingCommandBy\" TEXT;"); } catch { }
                    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Servers\" ADD COLUMN \"LastExecutedCommandId\" INTEGER NOT NULL DEFAULT 0;"); } catch { }
                    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Servers\" ADD COLUMN \"LastExecutedCommandAt\" TEXT;"); } catch { }
                    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Servers\" ADD COLUMN \"FolderStaleDaysJson\" TEXT;"); } catch { }
                    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Servers\" ADD COLUMN \"BackupShrinkFolder\" TEXT;"); } catch { }
                    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Servers\" ADD COLUMN \"LastBackupFilesJson\" TEXT;"); } catch { }
                    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Settings\" ADD COLUMN \"LastDailyReportDate\" TEXT;"); } catch { }
                    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Servers\" ADD COLUMN \"LastBootAt\" TEXT;"); } catch { }
                }
            }
            catch (Exception ex) { logger.LogWarning("Обновление схемы: {Msg}", ex.Message); }

            // Идемпотентное создание таблицы событий журнала Windows (на существующей БД EnsureCreated её не добавит).
            try
            {
                if (dbProvider == "postgres")
                {
                    db.Database.ExecuteSqlRaw(@"
CREATE TABLE IF NOT EXISTS ""Events"" (
    ""Id"" bigint GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
    ""ServerId"" integer NOT NULL,
    ""ReceivedAt"" timestamptz NOT NULL,
    ""TimeCreated"" timestamptz NOT NULL,
    ""LogName"" text NOT NULL DEFAULT '',
    ""Source"" text NOT NULL DEFAULT '',
    ""Level"" text NOT NULL DEFAULT '',
    ""EventId"" integer NOT NULL DEFAULT 0,
    ""Message"" text NOT NULL DEFAULT ''
);
CREATE INDEX IF NOT EXISTS ""IX_Events_ServerId_TimeCreated"" ON ""Events"" (""ServerId"", ""TimeCreated"");
DO $$ BEGIN
    ALTER TABLE ""Events"" ADD CONSTRAINT ""FK_Events_Servers_ServerId""
        FOREIGN KEY (""ServerId"") REFERENCES ""Servers"" (""Id"") ON DELETE CASCADE;
EXCEPTION WHEN duplicate_object THEN NULL; END $$;");
                }
                else
                {
                    db.Database.ExecuteSqlRaw(@"
CREATE TABLE IF NOT EXISTS ""Events"" (
    ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_Events"" PRIMARY KEY AUTOINCREMENT,
    ""ServerId"" INTEGER NOT NULL,
    ""ReceivedAt"" TEXT NOT NULL,
    ""TimeCreated"" TEXT NOT NULL,
    ""LogName"" TEXT NOT NULL DEFAULT '',
    ""Source"" TEXT NOT NULL DEFAULT '',
    ""Level"" TEXT NOT NULL DEFAULT '',
    ""EventId"" INTEGER NOT NULL DEFAULT 0,
    ""Message"" TEXT NOT NULL DEFAULT '',
    CONSTRAINT ""FK_Events_Servers_ServerId"" FOREIGN KEY (""ServerId"") REFERENCES ""Servers"" (""Id"") ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS ""IX_Events_ServerId_TimeCreated"" ON ""Events"" (""ServerId"", ""TimeCreated"");");
                }
            }
            catch (Exception ex) { logger.LogWarning("Создание таблицы Events: {Msg}", ex.Message); }

            // Идемпотентное создание таблицы журнала действий администратора.
            try
            {
                if (dbProvider == "postgres")
                {
                    db.Database.ExecuteSqlRaw(@"
CREATE TABLE IF NOT EXISTS ""Audit"" (
    ""Id"" bigint GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
    ""At"" timestamptz NOT NULL,
    ""Username"" text NOT NULL DEFAULT '',
    ""Action"" text NOT NULL DEFAULT '',
    ""Details"" text
);
CREATE INDEX IF NOT EXISTS ""IX_Audit_At"" ON ""Audit"" (""At"");");
                }
                else
                {
                    db.Database.ExecuteSqlRaw(@"
CREATE TABLE IF NOT EXISTS ""Audit"" (
    ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_Audit"" PRIMARY KEY AUTOINCREMENT,
    ""At"" TEXT NOT NULL,
    ""Username"" TEXT NOT NULL DEFAULT '',
    ""Action"" TEXT NOT NULL DEFAULT '',
    ""Details"" TEXT
);
CREATE INDEX IF NOT EXISTS ""IX_Audit_At"" ON ""Audit"" (""At"");");
                }
            }
            catch (Exception ex) { logger.LogWarning("Создание таблицы Audit: {Msg}", ex.Message); }

            // Идемпотентное создание таблицы журнала сервера (аутентификация, отчёты, ошибки).
            try
            {
                if (dbProvider == "postgres")
                {
                    db.Database.ExecuteSqlRaw(@"
CREATE TABLE IF NOT EXISTS ""Logs"" (
    ""Id"" bigint GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
    ""At"" timestamptz NOT NULL,
    ""Level"" text NOT NULL DEFAULT 'Information',
    ""Area"" integer NOT NULL DEFAULT 0,
    ""Message"" text NOT NULL DEFAULT '',
    ""Who"" text,
    ""Ip"" text,
    ""Details"" text
);
CREATE INDEX IF NOT EXISTS ""IX_Logs_At"" ON ""Logs"" (""At"");");
                }
                else
                {
                    db.Database.ExecuteSqlRaw(@"
CREATE TABLE IF NOT EXISTS ""Logs"" (
    ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_Logs"" PRIMARY KEY AUTOINCREMENT,
    ""At"" TEXT NOT NULL,
    ""Level"" TEXT NOT NULL DEFAULT 'Information',
    ""Area"" INTEGER NOT NULL DEFAULT 0,
    ""Message"" TEXT NOT NULL DEFAULT '',
    ""Who"" TEXT,
    ""Ip"" TEXT,
    ""Details"" TEXT
);
CREATE INDEX IF NOT EXISTS ""IX_Logs_At"" ON ""Logs"" (""At"");");
                }
            }
            catch (Exception ex) { logger.LogWarning("Создание таблицы Logs: {Msg}", ex.Message); }

            // Идемпотентное создание таблицы перезагрузок (история включений/выключений машин клиентов).
            try
            {
                if (dbProvider == "postgres")
                {
                    db.Database.ExecuteSqlRaw(@"
CREATE TABLE IF NOT EXISTS ""Reboots"" (
    ""Id"" bigint GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
    ""ServerId"" integer NOT NULL,
    ""At"" timestamptz NOT NULL,
    ""Kind"" text NOT NULL DEFAULT '',
    ""Reason"" text
);
CREATE INDEX IF NOT EXISTS ""IX_Reboots_ServerId_At"" ON ""Reboots"" (""ServerId"", ""At"");");
                }
                else
                {
                    db.Database.ExecuteSqlRaw(@"
CREATE TABLE IF NOT EXISTS ""Reboots"" (
    ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_Reboots"" PRIMARY KEY AUTOINCREMENT,
    ""ServerId"" INTEGER NOT NULL,
    ""At"" TEXT NOT NULL,
    ""Kind"" TEXT NOT NULL DEFAULT '',
    ""Reason"" TEXT
);
CREATE INDEX IF NOT EXISTS ""IX_Reboots_ServerId_At"" ON ""Reboots"" (""ServerId"", ""At"");");
                }
            }
            catch (Exception ex) { logger.LogWarning("Создание таблицы Reboots: {Msg}", ex.Message); }

            if (!await db.Settings.AnyAsync())
            {
                db.Settings.Add(new ServerSettings());
                await db.SaveChangesAsync();
            }
            logger.LogInformation("База данных готова ({Provider}).", dbProvider);
            break;
        }
        catch (Exception ex) when (attempt < maxAttempts)
        {
            logger.LogWarning("БД недоступна (попытка {Attempt}/{Max}): {Msg}. Повтор через 5 с.",
                attempt, maxAttempts, ex.Message);
            await Task.Delay(TimeSpan.FromSeconds(5));
        }
    }
}

// --- Разделение портов + IP-allowlist (самый ранний middleware) ---
app.Use(async (ctx, next) =>
{
    var localPort = ctx.Connection.LocalPort;
    var isApi = ctx.Request.Path.StartsWithSegments("/api");

    // Агентское API (/api/*) работает на ОБОИХ портах — старые агенты на 8080 не ломаются,
    // а порт 8081 выделен специально для агентов. Веб-интерфейс — только на веб-порту.
    if (ServerPorts.SeparatePorts && !isApi && localPort == ServerPorts.ReportsPort)
    {
        ctx.Response.StatusCode = 404;
        return;
    }

    // Список разрешённых IP применяется только к веб-страницам на веб-порту (не к API агентов).
    if (!isApi && localPort == ServerPorts.WebPort &&
        !IpAllowList.IsAllowed(ctx.Connection.RemoteIpAddress, allowedIps))
    {
        ctx.Response.StatusCode = 403;
        await ctx.Response.WriteAsync("Доступ запрещён (ваш IP не в списке разрешённых).");
        return;
    }

    await next();
});

if (!app.Environment.IsDevelopment())
    app.UseExceptionHandler("/Error");

app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.MapRazorPages();

// --- API приёма отчётов от агентов ---
app.MapPost("/api/report", async (HttpContext ctx, ReportIngestService ingest, ServerLogService logs) =>
{
    var ip = ServerLogService.ClientIp(ctx);
    var apiKey = ctx.Request.Headers["X-Api-Key"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(apiKey))
    {
        logs.Warn(LogArea.Reports, "Отчёт отклонён: нет заголовка X-Api-Key", null, ip);
        return Results.Json(new ReportAckDto { Accepted = false, Message = "Нет X-Api-Key" }, statusCode: 401);
    }

    AgentReportDto? report;
    try { report = await ctx.Request.ReadFromJsonAsync<AgentReportDto>(); }
    catch (Exception ex)
    {
        logs.Warn(LogArea.Reports, "Отчёт отклонён: неверный JSON", null, ip, ex.Message);
        return Results.Json(new ReportAckDto { Accepted = false, Message = "Неверный JSON" }, statusCode: 400);
    }

    if (report is null)
    {
        logs.Warn(LogArea.Reports, "Отчёт отклонён: пустое тело запроса", null, ip);
        return Results.Json(new ReportAckDto { Accepted = false, Message = "Пустой отчёт" }, statusCode: 400);
    }

    var ack = await ingest.IngestAsync(apiKey!, report);
    if (!ack.Accepted)
        logs.Warn(LogArea.Reports, "Отчёт отклонён: " + ack.Message, report.MachineName, ip,
            "ключ: …" + apiKey![Math.Max(0, apiKey.Length - 6)..]);
    else if (!report.IsHeartbeat)
        // Heartbeat приходит каждую минуту — его не логируем, иначе журнал заполнится им одним.
        logs.Info(LogArea.Reports, "Принят отчёт от агента", report.MachineName, ip,
            $"папок: {report.Folders.Count}, дисков: {report.Disks.Count}, версия агента: {report.AgentVersion}");

    return ack.Accepted ? Results.Ok(ack) : Results.Json(ack, statusCode: 403);
});

// Простой health-check для агента (проверка доступности сервера).
app.MapGet("/api/ping", () => Results.Ok(new { ok = true, time = DateTimeOffset.UtcNow }));

// --- Обновления агента ---
// Администратор кладёт установщик YpmonAgent-Setup.exe в папку agent-updates рядом с сервером,
// а также version.txt с номером версии.
var updatesDir = Path.Combine(contentRoot, "agent-updates");
Directory.CreateDirectory(updatesDir);
var agentInstaller = Path.Combine(updatesDir, "YpmonAgent-Setup.exe");
var agentVersionFile = Path.Combine(updatesDir, "version.txt");

// Проверка API-ключа агента по базе.
async Task<bool> ValidKey(HttpContext ctx, AppDbContext db)
{
    var key = ctx.Request.Headers["X-Api-Key"].FirstOrDefault();
    var ok = !string.IsNullOrWhiteSpace(key) && await db.Servers.AnyAsync(s => s.ApiKey == key);
    if (!ok)
        ctx.RequestServices.GetRequiredService<ServerLogService>().Warn(LogArea.Auth,
            $"Отказ в доступе к {ctx.Request.Path}: неверный API-ключ агента",
            null, ServerLogService.ClientIp(ctx));
    return ok;
}

app.MapGet("/api/agent/version", async (HttpContext ctx, AppDbContext db) =>
{
    if (!await ValidKey(ctx, db)) return Results.Unauthorized();
    if (!System.IO.File.Exists(agentInstaller))
        return Results.Json(new { available = false, version = (string?)null });
    string? ver = System.IO.File.Exists(agentVersionFile)
        ? (await System.IO.File.ReadAllTextAsync(agentVersionFile)).Trim()
        : null;
    return Results.Json(new { available = true, version = ver });
});

app.MapGet("/api/agent/download", async (HttpContext ctx, AppDbContext db) =>
{
    if (!await ValidKey(ctx, db)) return Results.Unauthorized();
    if (!System.IO.File.Exists(agentInstaller)) return Results.NotFound();
    return Results.File(agentInstaller, "application/octet-stream", "YpmonAgent-Setup.exe");
});

// Быстрый поиск сервера для строки поиска в шапке. Путь НЕ под /api — иначе он попал бы
// в агентский контур (порт отчётов, без фильтра по IP). Здесь нужен обычный вход в интерфейс.
app.MapGet("/search/servers", async (string? q, AppDbContext db) =>
{
    var term = (q ?? "").Trim();
    if (term.Length < 1) return Results.Json(Array.Empty<object>());

    var settings = await db.Settings.FirstOrDefaultAsync();
    var threshold = settings?.OfflineThresholdSeconds ?? 300;

    // Фильтруем в памяти, а не через LIKE: в SQL он регистрозависим для кириллицы
    // (и в sqlite, и в postgres), из-за чего «серв» не находил бы «Сервер».
    // Серверов сотни — выборка дешёвая.
    bool Match(string? v) => v is not null && v.Contains(term, StringComparison.OrdinalIgnoreCase);
    var servers = (await db.Servers.Include(s => s.Client).ToListAsync())
        .Where(s => Match(s.Name) || Match(s.LastMachineName) || Match(s.IpAddress) || Match(s.Client?.Name))
        .OrderBy(s => s.Name).Take(15).ToList();

    var items = servers.Select(s =>
    {
        var offline = s.IsOffline(threshold);
        // Статус тот же, что на дашборде: офлайн → проблема усадки → итог последнего отчёта.
        var status = offline ? "offline"
                   : s.BackupShrinkActive ? "problem"
                   : s.LastOutcome switch
                     {
                         JobOutcome.Ok => "ok",
                         JobOutcome.Warning => "problem",
                         JobOutcome.Error => "problem",
                         _ => "unknown"
                     };
        var label = offline ? "Офлайн"
                  : s.BackupShrinkActive ? "Объём упал"
                  : UiHelpers.OutcomeText(s.LastOutcome);
        return new
        {
            id = s.Id,
            name = s.Name,
            client = s.Client?.Name ?? "",
            machine = s.LastMachineName ?? "",
            ip = s.IpAddress ?? "",
            status,
            label
        };
    });
    return Results.Json(items);
}).RequireAuthorization();

// Скачивание установщика из веб-интерфейса (для авторизованного администратора, без API-ключа).
app.MapGet("/agent-installer", (HttpContext ctx) =>
{
    if (!System.IO.File.Exists(agentInstaller)) return Results.NotFound();
    return Results.File(agentInstaller, "application/octet-stream", "YpmonAgent-Setup.exe");
}).RequireAuthorization();

app.Run();
