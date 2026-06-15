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

// Порт веб-интерфейса (по умолчанию 8080).
var httpPort = builder.Configuration.GetValue<int?>("Server:HttpPort") ?? 8080;
builder.WebHost.UseUrls($"http://0.0.0.0:{httpPort}");

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
// Администратор кладёт новую версию Ypmon.Agent.exe в папку agent-updates рядом с сервером.
var updatesDir = Path.Combine(contentRoot, "agent-updates");
Directory.CreateDirectory(updatesDir);
var agentExePath = Path.Combine(updatesDir, "Ypmon.Agent.exe");

// Проверка API-ключа агента по базе.
async Task<bool> ValidKey(HttpContext ctx, AppDbContext db)
{
    var key = ctx.Request.Headers["X-Api-Key"].FirstOrDefault();
    return !string.IsNullOrWhiteSpace(key) && await db.Servers.AnyAsync(s => s.ApiKey == key);
}

app.MapGet("/api/agent/version", async (HttpContext ctx, AppDbContext db) =>
{
    if (!await ValidKey(ctx, db)) return Results.Unauthorized();
    if (!System.IO.File.Exists(agentExePath))
        return Results.Json(new { available = false, version = (string?)null });
    var ver = System.Diagnostics.FileVersionInfo.GetVersionInfo(agentExePath).FileVersion;
    return Results.Json(new { available = true, version = ver });
});

app.MapGet("/api/agent/download", async (HttpContext ctx, AppDbContext db) =>
{
    if (!await ValidKey(ctx, db)) return Results.Unauthorized();
    if (!System.IO.File.Exists(agentExePath)) return Results.NotFound();
    return Results.File(agentExePath, "application/octet-stream", "Ypmon.Agent.exe");
});

app.Run();
