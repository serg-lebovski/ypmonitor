using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Ypmon.Server.Data;
using Ypmon.Server.Services;

namespace Ypmon.Server.Pages;

public class SettingsModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly IConfiguration _cfg;
    private readonly AlertService _alerts;
    private readonly IWebHostEnvironment _env;
    private readonly ServerUpdateService _upd;
    public SettingsModel(AppDbContext db, IConfiguration cfg, AlertService alerts, IWebHostEnvironment env, ServerUpdateService upd)
    {
        _db = db; _cfg = cfg; _alerts = alerts; _env = env; _upd = upd;
    }

    public ServerSettings Settings { get; set; } = new();
    public List<AppUser> Users { get; set; } = new();
    public string? Message { get; set; }
    public bool IsError { get; set; }

    // Информация о сервере (из конфигурации, меняется в appsettings.json)
    public int HttpPort { get; set; }
    public string DbProvider { get; set; } = "sqlite";

    // Установщик агента (лежит в папке agent-updates рядом с сервером)
    public bool AgentInstallerExists { get; set; }
    public string? AgentInstallerVersion { get; set; }
    public long AgentInstallerSize { get; set; }
    public DateTime? AgentInstallerDate { get; set; }

    // Самообновление сервера (через git + воркер ypmon-updater)
    public bool UpdateConfigured { get; set; }
    public ServerUpdateStatus? UpdateStatus { get; set; }
    public string[] UpdateChangelog { get; set; } = Array.Empty<string>();

    private int CurrentUserId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "0");
    private bool IsAdmin => User.IsInRole("Admin");

    public async Task OnGetAsync() => await Load();

    private async Task Load()
    {
        Settings = await _db.Settings.FirstOrDefaultAsync() ?? new ServerSettings();
        Users = await _db.Users.OrderBy(u => u.Username).ToListAsync();
        HttpPort = _cfg.GetValue<int?>("Server:HttpPort") ?? 8080;
        DbProvider = _cfg["Database:Provider"] ?? "sqlite";

        var updatesDir = Path.Combine(_env.ContentRootPath, "agent-updates");
        var installer = Path.Combine(updatesDir, "YpmonAgent-Setup.exe");
        var verFile = Path.Combine(updatesDir, "version.txt");
        if (System.IO.File.Exists(installer))
        {
            AgentInstallerExists = true;
            var fi = new FileInfo(installer);
            AgentInstallerSize = fi.Length;
            AgentInstallerDate = fi.LastWriteTime;
            if (System.IO.File.Exists(verFile))
                AgentInstallerVersion = (await System.IO.File.ReadAllTextAsync(verFile)).Trim();
        }

        UpdateConfigured = _upd.Configured;
        if (UpdateConfigured)
        {
            UpdateStatus = _upd.ReadStatus();
            UpdateChangelog = _upd.ReadChangelog();
        }
    }

    public async Task<IActionResult> OnPostCheckUpdateAsync()
    {
        if (!IsAdmin) return Forbid();
        if (!_upd.Configured) { await Load(); IsError = true; Message = "Самообновление не настроено на сервере."; return Page(); }
        var since = _upd.ReadStatus()?.LastChecked ?? DateTimeOffset.MinValue;
        _upd.RequestCheck();
        var s = await _upd.WaitForFreshStatusAsync(since, TimeSpan.FromSeconds(20));
        await Load();
        if (s is null) { IsError = true; Message = "Воркер обновления не ответил. Проверьте службу ypmon-updater."; }
        else if (s.State == "error") { IsError = true; Message = s.Message ?? "Ошибка проверки обновлений."; }
        else Message = s.UpdateAvailable
            ? $"Доступно обновление: +{s.Behind} коммит(ов) (текущая {s.CurrentShort} → {s.RemoteShort}). Нажмите «Установить обновление»."
            : $"Установлена актуальная версия ({s.CurrentShort}).";
        return Page();
    }

    public async Task<IActionResult> OnPostApplyUpdateAsync()
    {
        if (!IsAdmin) return Forbid();
        if (!_upd.Configured) { await Load(); IsError = true; Message = "Самообновление не настроено на сервере."; return Page(); }
        _upd.RequestApply();
        await Load();
        Message = "Обновление запущено. Сервер выполнит git pull и пересоберётся (≈1–3 мин), затем перезапустится. " +
                  "Через пару минут обновите страницу.";
        return Page();
    }

    public async Task<IActionResult> OnPostSaveSettingsAsync(ServerSettings input)
    {
        if (!IsAdmin) return Forbid();
        var s = await _db.Settings.FirstOrDefaultAsync() ?? new ServerSettings();
        s.OfflineThresholdSeconds = Math.Max(60, input.OfflineThresholdSeconds);
        s.ReportRetentionDays = Math.Max(0, input.ReportRetentionDays);
        s.AlertsEnabled = input.AlertsEnabled;
        s.TelegramEnabled = input.TelegramEnabled;
        s.TelegramBotToken = input.TelegramBotToken;
        s.TelegramChatId = input.TelegramChatId;
        s.TelegramProxyUrl = input.TelegramProxyUrl;
        s.EmailEnabled = input.EmailEnabled;
        s.SmtpHost = input.SmtpHost;
        s.SmtpPort = input.SmtpPort;
        s.SmtpUseSsl = input.SmtpUseSsl;
        s.SmtpUser = input.SmtpUser;
        s.SmtpPassword = input.SmtpPassword;
        s.EmailFrom = input.EmailFrom;
        s.EmailTo = input.EmailTo;
        if (s.Id == 0) _db.Settings.Add(s);
        await _db.SaveChangesAsync();
        await Load();
        Message = "Настройки сохранены";
        return Page();
    }

    public async Task<IActionResult> OnPostTestAlertAsync()
    {
        if (!IsAdmin) return Forbid();
        var s = await _db.Settings.FirstOrDefaultAsync() ?? new ServerSettings();
        await _alerts.SendAsync(s, "YPMon: тестовое оповещение", "Если вы это видите — оповещения настроены верно.");
        await Load();
        Message = "Тестовое оповещение отправлено (проверьте Telegram / e-mail)";
        return Page();
    }

    public async Task<IActionResult> OnPostSaveProfileAsync(string displayName, string username, string? newPassword)
    {
        var u = await _db.Users.FindAsync(CurrentUserId);
        if (u is null) return RedirectToPage();

        if (!string.IsNullOrWhiteSpace(username) && username.Trim() != u.Username)
        {
            if (await _db.Users.AnyAsync(x => x.Username == username.Trim() && x.Id != u.Id))
            {
                await Load(); IsError = true; Message = "Логин уже занят"; return Page();
            }
            u.Username = username.Trim();
        }
        if (!string.IsNullOrWhiteSpace(displayName)) u.DisplayName = displayName.Trim();
        if (!string.IsNullOrWhiteSpace(newPassword))
        {
            if (newPassword.Length < 6) { await Load(); IsError = true; Message = "Пароль не короче 6 символов"; return Page(); }
            var (h, salt) = PasswordHasher.Hash(newPassword);
            u.PasswordHash = h; u.PasswordSalt = salt;
        }
        await _db.SaveChangesAsync();
        await Load();
        Message = "Профиль обновлён. При смене логина войдите заново при следующем входе.";
        return Page();
    }

    public async Task<IActionResult> OnPostAddUserAsync(string username, string displayName, string password, string role)
    {
        if (!IsAdmin) return Forbid();
        if (string.IsNullOrWhiteSpace(username) || password.Length < 6)
        {
            await Load(); IsError = true; Message = "Логин обязателен, пароль не короче 6 символов"; return Page();
        }
        if (await _db.Users.AnyAsync(x => x.Username == username.Trim()))
        {
            await Load(); IsError = true; Message = "Такой логин уже есть"; return Page();
        }
        var (h, salt) = PasswordHasher.Hash(password);
        _db.Users.Add(new AppUser
        {
            Username = username.Trim(),
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? username.Trim() : displayName.Trim(),
            PasswordHash = h, PasswordSalt = salt,
            Role = role == "Admin" ? UserRole.Admin : UserRole.Viewer
        });
        await _db.SaveChangesAsync();
        await Load();
        Message = "Пользователь добавлен";
        return Page();
    }

    public async Task<IActionResult> OnPostEditUserAsync(int userId, string displayName, string role, string? newPassword)
    {
        if (!IsAdmin) return Forbid();
        var u = await _db.Users.FindAsync(userId);
        if (u is null) return RedirectToPage();

        if (!string.IsNullOrWhiteSpace(displayName)) u.DisplayName = displayName.Trim();
        // Нельзя снять с себя роль администратора, если других админов нет
        var newRole = role == "Admin" ? UserRole.Admin : UserRole.Viewer;
        if (u.Id == CurrentUserId && newRole != UserRole.Admin &&
            !await _db.Users.AnyAsync(x => x.Id != u.Id && x.Role == UserRole.Admin))
        {
            await Load(); IsError = true; Message = "Нельзя снять роль администратора с единственного админа"; return Page();
        }
        u.Role = newRole;

        if (!string.IsNullOrWhiteSpace(newPassword))
        {
            if (newPassword.Length < 6) { await Load(); IsError = true; Message = "Пароль не короче 6 символов"; return Page(); }
            var (h, salt) = PasswordHasher.Hash(newPassword);
            u.PasswordHash = h; u.PasswordSalt = salt;
        }
        await _db.SaveChangesAsync();
        await Load();
        Message = "Пользователь обновлён";
        return Page();
    }

    public async Task<IActionResult> OnPostDeleteUserAsync(int userId)
    {
        if (!IsAdmin) return Forbid();
        if (userId == CurrentUserId) { await Load(); IsError = true; Message = "Нельзя удалить самого себя"; return Page(); }
        if (await _db.Users.CountAsync() <= 1) { await Load(); IsError = true; Message = "Нельзя удалить последнего пользователя"; return Page(); }
        var u = await _db.Users.FindAsync(userId);
        if (u is not null) { _db.Users.Remove(u); await _db.SaveChangesAsync(); }
        await Load();
        Message = "Пользователь удалён";
        return Page();
    }
}
