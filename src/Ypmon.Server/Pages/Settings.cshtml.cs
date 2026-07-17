using System.Net.Http;
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
    private readonly TelegramService _tg;
    private readonly TelegramReportService _report;
    private readonly WireGuardProxyService _wg;
    private readonly AuditService _audit;
    public SettingsModel(AppDbContext db, IConfiguration cfg, AlertService alerts, IWebHostEnvironment env,
        ServerUpdateService upd, TelegramService tg, TelegramReportService report, WireGuardProxyService wg, AuditService audit)
    {
        _db = db; _cfg = cfg; _alerts = alerts; _env = env; _upd = upd; _tg = tg; _report = report; _wg = wg; _audit = audit;
    }

    public bool WireGuardRunning { get; set; }
    public string WireGuardStatus { get; set; } = "";

    public ServerSettings Settings { get; set; } = new();
    public List<AppUser> Users { get; set; } = new();
    public List<AuditEntry> AuditLog { get; set; } = new();
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
        if (IsAdmin)
            AuditLog = await _db.Audit.OrderByDescending(a => a.At).Take(200).ToListAsync();
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
        WireGuardRunning = _wg.IsRunning;
        WireGuardStatus = _wg.LastStatus;
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
        await _audit.LogAsync(User, "Запущено обновление сервера (git pull + пересборка)");
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
        s.DefaultBackupStaleDays = Math.Max(0, input.DefaultBackupStaleDays);
        s.WindowsErrorIgnore = input.WindowsErrorIgnore;
        s.AlertsEnabled = input.AlertsEnabled;
        // Настройки Telegram сохраняются отдельным обработчиком SaveTelegram — здесь их не трогаем.
        s.EmailEnabled = input.EmailEnabled;
        s.SmtpHost = input.SmtpHost;
        s.SmtpPort = input.SmtpPort;
        s.SmtpUseSsl = input.SmtpUseSsl;
        s.SmtpUser = input.SmtpUser;
        s.SmtpPassword = input.SmtpPassword;
        s.EmailFrom = input.EmailFrom;
        s.EmailTo = input.EmailTo;
        s.ArchiveReportEmailTo = input.ArchiveReportEmailTo;
        s.SmartAlertsEnabled = input.SmartAlertsEnabled;
        s.SmartAlertOnWarning = input.SmartAlertOnWarning;
        s.SmartTempThresholdC = Math.Clamp(input.SmartTempThresholdC, 0, 120);
        if (s.Id == 0) _db.Settings.Add(s);
        await _db.SaveChangesAsync();
        await _audit.LogAsync(User, "Изменены настройки", "оповещения / SMTP / хранение истории");
        await Load();
        Message = "Настройки сохранены";
        return Page();
    }

    public async Task<IActionResult> OnPostSaveServerAddressAsync(string? externalAddress, int externalPort)
    {
        if (!IsAdmin) return Forbid();
        var s = await _db.Settings.FirstOrDefaultAsync() ?? new ServerSettings();
        s.ExternalAddress = string.IsNullOrWhiteSpace(externalAddress) ? null : externalAddress.Trim();
        s.ExternalPort = externalPort is > 0 and < 65536 ? externalPort : 8081;
        if (s.Id == 0) _db.Settings.Add(s);
        await _db.SaveChangesAsync();
        await _audit.LogAsync(User, "Изменён внешний адрес сервера", $"{s.ExternalAddress}:{s.ExternalPort}");
        await Load();
        Message = "Внешний адрес сервера сохранён.";
        return Page();
    }

    public async Task<IActionResult> OnPostSaveWireGuardAsync(IFormFile? wgFile, bool wgEnabled, string? wgConfigText)
    {
        if (!IsAdmin) return Forbid();
        var s = await _db.Settings.FirstOrDefaultAsync() ?? new ServerSettings();
        // Приоритет: загруженный файл → вставленный текст. Так проще заменить конфиг при смене VPN-сервера Amnezia.
        if (wgFile is { Length: > 0 })
        {
            using var reader = new StreamReader(wgFile.OpenReadStream());
            s.WireGuardConfig = await reader.ReadToEndAsync();
        }
        else if (!string.IsNullOrWhiteSpace(wgConfigText))
        {
            s.WireGuardConfig = wgConfigText.Trim();
        }
        s.WireGuardEnabled = wgEnabled;
        if (s.Id == 0) _db.Settings.Add(s);
        await _db.SaveChangesAsync();
        _wg.Apply(s.WireGuardEnabled, s.WireGuardConfig);
        await _audit.LogAsync(User, "Обновлён конфиг обхода блокировки Telegram", s.WireGuardEnabled ? "включён" : "выключен");
        await Load();
        Message = "WireGuard: " + _wg.LastStatus;
        IsError = s.WireGuardEnabled && !_wg.IsRunning;
        return Page();
    }

    public async Task<IActionResult> OnPostDeleteWireGuardAsync()
    {
        if (!IsAdmin) return Forbid();
        var s = await _db.Settings.FirstOrDefaultAsync();
        if (s is not null)
        {
            s.WireGuardConfig = null;
            s.WireGuardEnabled = false;
            await _db.SaveChangesAsync();
        }
        _wg.Apply(false, null);   // остановить туннель и убрать конфиг
        await _audit.LogAsync(User, "Удалён конфиг обхода блокировки Telegram");
        await Load();
        Message = "Туннель WireGuard остановлен, конфигурация удалена.";
        return Page();
    }

    public async Task<IActionResult> OnPostSaveTelegramAsync(ServerSettings input)
    {
        if (!IsAdmin) return Forbid();
        var s = await _db.Settings.FirstOrDefaultAsync() ?? new ServerSettings();
        s.TelegramEnabled = input.TelegramEnabled;
        s.TelegramBotToken = input.TelegramBotToken;
        s.TelegramChatId = input.TelegramChatId;
        s.PublicBaseUrl = input.PublicBaseUrl;
        s.DailyReportEnabled = input.DailyReportEnabled;
        s.DailyReportHourOmsk = Math.Clamp(input.DailyReportHourOmsk, 0, 23);
        if (s.Id == 0) _db.Settings.Add(s);
        await _db.SaveChangesAsync();
        await _audit.LogAsync(User, "Изменены настройки Telegram-бота");
        await Load();
        Message = "Настройки бота сохранены.";
        return Page();
    }

    public async Task<IActionResult> OnPostCheckWireGuardAsync()
    {
        if (!IsAdmin) return Forbid();
        await Load();
        if (!_wg.IsRunning) { IsError = true; Message = "WireGuard не запущен. Загрузите конфиг, включите и примените."; return Page(); }
        try
        {
            var handler = new HttpClientHandler { Proxy = new System.Net.WebProxy(_wg.SocksProxyUrl), UseProxy = true };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
            var ip = (await http.GetStringAsync("https://api.ipify.org")).Trim();
            Message = $"Внешний IP через WireGuard: {ip}";
        }
        catch (Exception ex) { IsError = true; Message = "Не удалось получить внешний IP через WireGuard: " + ex.Message; }
        return Page();
    }

    public async Task<IActionResult> OnPostDiscoverChatsAsync()
    {
        if (!IsAdmin) return Forbid();
        var s = await _db.Settings.FirstOrDefaultAsync() ?? new ServerSettings();
        Message = await _tg.DiscoverChatsAsync(s);
        await Load();
        return Page();
    }

    public async Task<IActionResult> OnPostSendReportNowAsync()
    {
        if (!IsAdmin) return Forbid();
        Message = await _report.SendReportAsync();
        await _audit.LogAsync(User, "Ручная рассылка отчёта об архивации");
        await Load();
        return Page();
    }

    public async Task<IActionResult> OnPostCreateTopicsAsync()
    {
        if (!IsAdmin) return Forbid();
        var s = await _db.Settings.FirstOrDefaultAsync();
        if (s is null || !s.TelegramEnabled) { await Load(); IsError = true; Message = "Telegram выключен."; return Page(); }
        var clients = await _db.Clients.ToListAsync();
        int made = 0;
        foreach (var c in clients.Where(c => c.TelegramTopicId is null))
            if (await _report.EnsureTopicAsync(s, c) is not null) made++;
        await Load();
        Message = $"Темы созданы/проверены. Новых тем: {made}.";
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

    private static UserRole ParseRole(string? role) => role switch
    {
        "Admin" => UserRole.Admin,
        "Engineer" => UserRole.Engineer,
        _ => UserRole.Viewer
    };

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
            Role = ParseRole(role)
        });
        await _db.SaveChangesAsync();
        await _audit.LogAsync(User, "Добавлен пользователь", $"{username.Trim()} — роль {UiHelpers.RoleName(ParseRole(role))}");
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
        var newRole = ParseRole(role);
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
        await _audit.LogAsync(User, "Изменён пользователь", $"{u.Username} — роль {UiHelpers.RoleName(u.Role)}"
            + (string.IsNullOrWhiteSpace(newPassword) ? "" : ", сменён пароль"));
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
        if (u is not null)
        {
            var uname = u.Username;
            _db.Users.Remove(u);
            await _db.SaveChangesAsync();
            await _audit.LogAsync(User, "Удалён пользователь", uname);
        }
        await Load();
        Message = "Пользователь удалён";
        return Page();
    }
}
