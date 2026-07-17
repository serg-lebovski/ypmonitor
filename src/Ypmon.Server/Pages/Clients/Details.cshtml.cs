using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Ypmon.Server.Data;
using Ypmon.Server.Services;

namespace Ypmon.Server.Pages.Clients;

public class DetailsModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly IWebHostEnvironment _env;
    public DetailsModel(AppDbContext db, IWebHostEnvironment env) { _db = db; _env = env; }

    public Client? Client { get; set; }
    public int OfflineThreshold { get; set; } = 300;

    // Установщик агента (лежит в папке agent-updates рядом с сервером) — для кнопки «Скачать агента».
    public bool AgentInstallerExists { get; set; }
    public string? AgentInstallerVersion { get; set; }

    [BindProperty(SupportsGet = true)] public int Id { get; set; }

    [BindProperty] public string ServerName { get; set; } = "";
    [BindProperty] public string? PhysicalAddress { get; set; }
    [BindProperty] public string? IpAddress { get; set; }
    [BindProperty] public string? ExternalIpAddress { get; set; }
    [BindProperty] public string? ServerDescription { get; set; }

    // Редактирование клиента
    [BindProperty] public string ClientName { get; set; } = "";
    [BindProperty] public string? ClientDescription { get; set; }
    [BindProperty] public string? RouterAddress { get; set; }
    [BindProperty] public int RouterPingIntervalSeconds { get; set; } = 60;
    public string? Message { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        await Load();
        if (Client is null) return NotFound();
        return Page();
    }

    private async Task Load()
    {
        OfflineThreshold = (await _db.Settings.FirstOrDefaultAsync())?.OfflineThresholdSeconds ?? 300;
        Client = await _db.Clients.Include(c => c.Servers).FirstOrDefaultAsync(c => c.Id == Id);
        if (Client is not null)
            Client.Servers = Client.Servers.OrderBy(s => s.Name).ToList();

        var updatesDir = Path.Combine(_env.ContentRootPath, "agent-updates");
        var installer = Path.Combine(updatesDir, "YpmonAgent-Setup.exe");
        var verFile = Path.Combine(updatesDir, "version.txt");
        if (System.IO.File.Exists(installer))
        {
            AgentInstallerExists = true;
            if (System.IO.File.Exists(verFile))
                AgentInstallerVersion = (await System.IO.File.ReadAllTextAsync(verFile)).Trim();
        }
    }

    public async Task<IActionResult> OnPostSaveClientAsync()
    {
        if (!User.CanEdit()) return Forbid();
        var c = await _db.Clients.FindAsync(Id);
        if (c is null) return NotFound();
        if (!string.IsNullOrWhiteSpace(ClientName)) c.Name = ClientName.Trim();
        c.Description = ClientDescription?.Trim();
        var newRouter = string.IsNullOrWhiteSpace(RouterAddress) ? null : RouterAddress.Trim();
        if (newRouter != c.RouterAddress)
        {
            // Адрес сменили/убрали — старое состояние тревоги и результата больше не актуально.
            c.AlertRouterPingActive = false;
            c.LastRouterPingOk = null;
            c.LastRouterPingAt = null;
        }
        c.RouterAddress = newRouter;
        c.RouterPingIntervalSeconds = Math.Clamp(RouterPingIntervalSeconds, 10, 86400);
        await _db.SaveChangesAsync();
        await Load();
        Message = "Клиент сохранён";
        return Page();
    }

    public async Task<IActionResult> OnPostAddServerAsync()
    {
        if (!User.CanEdit()) return Forbid();
        if (!string.IsNullOrWhiteSpace(ServerName))
        {
            _db.Servers.Add(new MonitoredServer
            {
                ClientId = Id,
                Name = ServerName.Trim(),
                PhysicalAddress = PhysicalAddress?.Trim(),
                IpAddress = IpAddress?.Trim(),
                ExternalIpAddress = string.IsNullOrWhiteSpace(ExternalIpAddress) ? null : ExternalIpAddress.Trim(),
                Description = ServerDescription?.Trim(),
                ApiKey = PasswordHasher.NewApiKey()
            });
            await _db.SaveChangesAsync();
        }
        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnPostDeleteServerAsync(int serverId)
    {
        if (!User.CanEdit()) return Forbid();
        var s = await _db.Servers.FindAsync(serverId);
        if (s is not null) { _db.Servers.Remove(s); await _db.SaveChangesAsync(); }
        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnPostRegenKeyAsync(int serverId)
    {
        if (!User.CanEdit()) return Forbid();
        var s = await _db.Servers.FindAsync(serverId);
        if (s is not null) { s.ApiKey = PasswordHasher.NewApiKey(); await _db.SaveChangesAsync(); }
        return RedirectToPage(new { id = Id });
    }
}
