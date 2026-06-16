using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;
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

builder.Services.AddHttpClient();
builder.Services.AddScoped<AlertService>();
builder.Services.AddScoped<ReportIngestService>();
builder.Services.AddHostedService<MaintenanceService>();

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
                    db.Database.ExecuteSqlRaw("ALTER TABLE \"Settings\" ADD COLUMN IF NOT EXISTS \"TelegramProxyUrl\" text;");
                else
                    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Settings\" ADD COLUMN \"TelegramProxyUrl\" TEXT;"); } catch { }
            }
            catch (Exception ex) { logger.LogWarning("Обновление схемы: {Msg}", ex.Message); }

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
app.MapPost("/api/report", async (HttpContext ctx, ReportIngestService ingest) =>
{
    var apiKey = ctx.Request.Headers["X-Api-Key"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(apiKey))
        return Results.Json(new ReportAckDto { Accepted = false, Message = "Нет X-Api-Key" }, statusCode: 401);

    AgentReportDto? report;
    try { report = await ctx.Request.ReadFromJsonAsync<AgentReportDto>(); }
    catch { return Results.Json(new ReportAckDto { Accepted = false, Message = "Неверный JSON" }, statusCode: 400); }

    if (report is null)
        return Results.Json(new ReportAckDto { Accepted = false, Message = "Пустой отчёт" }, statusCode: 400);

    var ack = await ingest.IngestAsync(apiKey!, report);
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
    return !string.IsNullOrWhiteSpace(key) && await db.Servers.AnyAsync(s => s.ApiKey == key);
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

// Скачивание установщика из веб-интерфейса (для авторизованного администратора, без API-ключа).
app.MapGet("/agent-installer", (HttpContext ctx) =>
{
    if (!System.IO.File.Exists(agentInstaller)) return Results.NotFound();
    return Results.File(agentInstaller, "application/octet-stream", "YpmonAgent-Setup.exe");
}).RequireAuthorization();

app.Run();
