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
builder.Services.AddSingleton<ApiAccessGuard>();

// Ограничитель частоты для агентского API: щедрый лимит на IP (на одном внешнем IP может
// сидеть несколько агентов — не режем их), но защищает от флуда. Не-API пути не ограничиваем.
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.GlobalLimiter = System.Threading.RateLimiting.PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
    {
        if (!ctx.Request.Path.StartsWithSegments("/api"))
            return System.Threading.RateLimiting.RateLimitPartition.GetNoLimiter("noapi");
        var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(ip, _ =>
            new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
            {
                PermitLimit = 300,                         // 300 запросов/мин на IP — агентам хватает с запасом
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            });
    });
});
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
                    db.Database.ExecuteSqlRaw("ALTER TABLE \"Servers\" ADD COLUMN IF NOT EXISTS \"AlertSnoozeUntil\" timestamptz;");
                    db.Database.ExecuteSqlRaw("ALTER TABLE \"Servers\" ADD COLUMN IF NOT EXISTS \"AlertSnoozeReason\" text;");
                    db.Database.ExecuteSqlRaw("ALTER TABLE \"Servers\" ADD COLUMN IF NOT EXISTS \"MaintenanceWindowsJson\" text;");
                    db.Database.ExecuteSqlRaw("ALTER TABLE \"Settings\" ADD COLUMN IF NOT EXISTS \"WebAllowedIps\" text;");
                    db.Database.ExecuteSqlRaw("ALTER TABLE \"Settings\" ADD COLUMN IF NOT EXISTS \"ApiIpFilterMode\" text NOT NULL DEFAULT 'Off';");
                    db.Database.ExecuteSqlRaw("ALTER TABLE \"Settings\" ADD COLUMN IF NOT EXISTS \"ApiAllowedIpsExtra\" text;");
                    db.Database.ExecuteSqlRaw("ALTER TABLE \"Servers\" ADD COLUMN IF NOT EXISTS \"FolderBackupTypesJson\" text;");
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
                    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Servers\" ADD COLUMN \"AlertSnoozeUntil\" TEXT;"); } catch { }
                    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Servers\" ADD COLUMN \"AlertSnoozeReason\" TEXT;"); } catch { }
                    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Servers\" ADD COLUMN \"MaintenanceWindowsJson\" TEXT;"); } catch { }
                    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Settings\" ADD COLUMN \"WebAllowedIps\" TEXT;"); } catch { }
                    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Settings\" ADD COLUMN \"ApiIpFilterMode\" TEXT NOT NULL DEFAULT 'Off';"); } catch { }
                    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Settings\" ADD COLUMN \"ApiAllowedIpsExtra\" TEXT;"); } catch { }
                    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Servers\" ADD COLUMN \"FolderBackupTypesJson\" TEXT;"); } catch { }
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

    if (!isApi && localPort == ServerPorts.WebPort)
    {
        // Белый список веб-порта: сначала из настроек (ведётся в интерфейсе), иначе из конфига.
        var webList = allowedIps;
        try
        {
            var db = ctx.RequestServices.GetRequiredService<AppDbContext>();
            var fromDb = (await db.Settings.AsNoTracking().FirstOrDefaultAsync())?.WebAllowedIps;
            var parsed = IpAllowList.Parse(fromDb);
            if (parsed.Count > 0) webList = parsed;
        }
        catch { /* БД недоступна — используем список из конфига */ }

        if (!IpAllowList.IsAllowed(ctx.Connection.RemoteIpAddress, webList))
        {
            ctx.Response.StatusCode = 403;
            await ctx.Response.WriteAsync("Доступ запрещён (ваш IP не в списке разрешённых).");
            return;
        }
    }

    // Фильтр доступа к API агентов по белому списку (приватные ∪ внешние IP серверов ∪ доп.список).
    // По умолчанию режим Off — пускаем всех, агенты не отваливаются.
    if (isApi)
    {
        var guard = ctx.RequestServices.GetRequiredService<ApiAccessGuard>();
        var decision = guard.Check(ctx.Connection.RemoteIpAddress);
        if (decision != ApiAccess.Allow)
        {
            var ip = ServerLogService.ClientIp(ctx);
            var logs = ctx.RequestServices.GetRequiredService<ServerLogService>();
            if (decision == ApiAccess.Block)
            {
                logs.Warn(LogArea.Auth, "Доступ к API заблокирован: IP не в белом списке", null, ip, ctx.Request.Path);
                ctx.Response.StatusCode = 403;
                await ctx.Response.WriteAsync("Доступ запрещён (IP не в белом списке).");
                return;
            }
            // Audit: не блокируем, только отмечаем — чтобы было видно, кого отрезало бы «Блокировкой».
            logs.Warn(LogArea.Auth, "Аудит доступа к API: незнакомый IP (пропущен)", null, ip, ctx.Request.Path);
        }
    }

    await next();
});

if (!app.Environment.IsDevelopment())
    app.UseExceptionHandler("/Error");

app.UseStaticFiles();
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapRazorPages();

// --- API приёма отчётов от агентов ---
app.MapPost("/api/report", async (HttpContext ctx, ReportIngestService ingest, ServerLogService logs) =>
{
    var ip = ServerLogService.ClientIp(ctx);

    // Ограничиваем размер тела отчёта: реальные отчёты — килобайты, а без лимита можно было
    // заставить сервер парсить до ~30 МБ мусора (амплификация DoS). 2 МБ — с большим запасом.
    const long maxBody = 2 * 1024 * 1024;
    var bodyLimit = ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
    if (bodyLimit is { IsReadOnly: false }) bodyLimit.MaxRequestBodySize = maxBody;
    if (ctx.Request.ContentLength is > maxBody)
    {
        logs.Warn(LogArea.Reports, "Отчёт отклонён: тело больше лимита", null, ip);
        return Results.Json(new ReportAckDto { Accepted = false, Message = "Слишком большой отчёт" }, statusCode: 413);
    }

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

// Быстрый поиск сервера/компании для строки поиска в шапке. Путь НЕ под /api — иначе он попал бы
// в агентский контур (порт отчётов, без фильтра по IP). Здесь нужен обычный вход в интерфейс.
app.MapGet("/search/servers", async (string? q, AppDbContext db) =>
{
    var term = (q ?? "").Trim();
    if (term.Length < 1) return Results.Json(Array.Empty<object>());

    var settings = await db.Settings.FirstOrDefaultAsync();
    var threshold = settings?.OfflineThresholdSeconds ?? 300;

    // Фильтруем в памяти, а не через LIKE: в SQL он регистрозависим для кириллицы
    // (и в sqlite, и в postgres), из-за чего «серв» не находил бы «Сервер».
    // Серверов/клиентов сотни — выборка дешёвая.
    bool Match(string? v) => v is not null && v.Contains(term, StringComparison.OrdinalIgnoreCase);

    string StatusOf(MonitoredServer s) =>
        s.IsOffline(threshold) ? "offline"
        : s.BackupShrinkActive ? "problem"
        : s.LastOutcome switch
          {
              JobOutcome.Ok => "ok",
              JobOutcome.Warning => "problem",
              JobOutcome.Error => "problem",
              _ => "unknown"
          };
    string LabelOf(MonitoredServer s) =>
        s.IsOffline(threshold) ? "Офлайн"
        : s.BackupShrinkActive ? "Объём упал"
        : UiHelpers.OutcomeText(s.LastOutcome);

    // Компании ищем отдельно от серверов: раньше совпадение с именем клиента только фильтровало
    // ЕГО серверы, а сама компания как результат никогда не показывалась — если у неё не было
    // подходящего по имени сервера (или серверов вообще не было), поиск по названию компании
    // ничего не находил.
    var clients = (await db.Clients.Include(c => c.Servers).ToListAsync())
        .Where(c => Match(c.Name))
        .OrderBy(c => c.Name).Take(8)
        .Select(c => new
        {
            type = "client",
            id = c.Id,
            name = c.Name,
            client = "",
            machine = c.Servers.Count == 1 ? "1 сервер" : $"{c.Servers.Count} серверов",
            ip = "",
            status = c.Servers.Count == 0 ? "unknown"
                   : c.Servers.Any(s => s.IsOffline(threshold)) ? "offline"
                   : c.Servers.Any(s => StatusOf(s) == "problem") ? "problem"
                   : c.Servers.All(s => StatusOf(s) == "ok") ? "ok" : "unknown",
            label = "Компания"
        });

    var servers = (await db.Servers.Include(s => s.Client).ToListAsync())
        .Where(s => Match(s.Name) || Match(s.LastMachineName) || Match(s.IpAddress) || Match(s.Client?.Name))
        .OrderBy(s => s.Name).Take(15)
        .Select(s => new
        {
            type = "server",
            id = s.Id,
            name = s.Name,
            client = s.Client?.Name ?? "",
            machine = s.LastMachineName ?? "",
            ip = s.IpAddress ?? "",
            status = StatusOf(s),
            label = LabelOf(s)
        });

    return Results.Json(clients.Concat(servers));
}).RequireAuthorization();

// Живое состояние дашборда для точечного обновления блоков без перезагрузки страницы.
// Вне /api (это UI-эндпоинт), под авторизацией. Считает статусы теми же помощниками, что и страница.
app.MapGet("/dash/state", async (AppDbContext db) =>
{
    var settings = await db.Settings.FirstOrDefaultAsync();
    var threshold = settings?.OfflineThresholdSeconds ?? 300;
    var now = DateTimeOffset.UtcNow;

    var clients = await db.Clients.Include(c => c.Servers).OrderBy(c => c.Name).ToListAsync();
    var servers = clients.SelectMany(c => c.Servers).ToList();

    int ok = 0, problem = 0, offline = 0;
    var srvMap = new Dictionary<string, object>();
    foreach (var s in servers)
    {
        var (status, cls, ico, word) = UiHelpers.TileStatus(s, threshold);
        if (status == "offline") offline++;
        else if (status == "ok") ok++;
        else if (status == "problem") problem++;
        srvMap[s.Id.ToString()] = new
        {
            status, cls, ico, word,
            ago = UiHelpers.Ago(s.LastSeenAt, now),
            dot = UiHelpers.DotCss(s.IsOffline(threshold) ? JobOutcome.Unknown : s.LastOutcome),
            badgeCss = s.IsOffline(threshold) ? "st-offline" : UiHelpers.OutcomeCss(s.LastOutcome),
            badgeText = s.IsOffline(threshold) ? "Офлайн" : UiHelpers.OutcomeText(s.LastOutcome),
            shrink = s.BackupShrinkActive,
            muted = AlertSuppression.MuteReason(s, now) is not null
        };
    }

    var clientMap = clients.ToDictionary(c => c.Id.ToString(), c =>
    {
        var off = c.Servers.Count(x => x.IsOffline(threshold));
        var prob = c.Servers.Count(x => !x.IsOffline(threshold) && x.LastOutcome >= JobOutcome.Warning);
        var shr = c.Servers.Count(x => x.BackupShrinkActive);
        return (object)new { off, prob, shrink = shr, allOk = off == 0 && prob == 0 && shr == 0 && c.Servers.Count > 0 };
    });

    return Results.Json(new
    {
        at = now.UtcDateTime.AddHours(6).ToString("HH:mm:ss"),
        counts = new { total = servers.Count, ok, problem, offline },
        servers = srvMap,
        clients = clientMap
    });
}).RequireAuthorization();

// Скачивание установщика из веб-интерфейса (для авторизованного администратора, без API-ключа).
app.MapGet("/agent-installer", (HttpContext ctx) =>
{
    if (!System.IO.File.Exists(agentInstaller)) return Results.NotFound();
    return Results.File(agentInstaller, "application/octet-stream", "YpmonAgent-Setup.exe");
}).RequireAuthorization();

app.Run();
