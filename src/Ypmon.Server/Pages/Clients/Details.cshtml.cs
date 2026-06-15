using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Ypmon.Server.Data;
using Ypmon.Server.Services;

namespace Ypmon.Server.Pages.Clients;

public class DetailsModel : PageModel
{
    private readonly AppDbContext _db;
    public DetailsModel(AppDbContext db) => _db = db;

    public Client? Client { get; set; }
    public int OfflineThreshold { get; set; } = 300;

    [BindProperty(SupportsGet = true)] public int Id { get; set; }

    [BindProperty] public string ServerName { get; set; } = "";
    [BindProperty] public string? PhysicalAddress { get; set; }
    [BindProperty] public string? IpAddress { get; set; }
    [BindProperty] public string? ServerDescription { get; set; }

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
    }

    public async Task<IActionResult> OnPostAddServerAsync()
    {
        if (!string.IsNullOrWhiteSpace(ServerName))
        {
            _db.Servers.Add(new MonitoredServer
            {
                ClientId = Id,
                Name = ServerName.Trim(),
                PhysicalAddress = PhysicalAddress?.Trim(),
                IpAddress = IpAddress?.Trim(),
                Description = ServerDescription?.Trim(),
                ApiKey = PasswordHasher.NewApiKey()
            });
            await _db.SaveChangesAsync();
        }
        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnPostDeleteServerAsync(int serverId)
    {
        var s = await _db.Servers.FindAsync(serverId);
        if (s is not null) { _db.Servers.Remove(s); await _db.SaveChangesAsync(); }
        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnPostRegenKeyAsync(int serverId)
    {
        var s = await _db.Servers.FindAsync(serverId);
        if (s is not null) { s.ApiKey = PasswordHasher.NewApiKey(); await _db.SaveChangesAsync(); }
        return RedirectToPage(new { id = Id });
    }
}
