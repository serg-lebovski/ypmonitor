using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Ypmon.Server.Data;
using Ypmon.Shared;

namespace Ypmon.Server.Pages.Servers;

public class DetailsModel : PageModel
{
    private readonly AppDbContext _db;
    public DetailsModel(AppDbContext db) => _db = db;

    [BindProperty(SupportsGet = true)] public int Id { get; set; }

    public MonitoredServer? Server { get; set; }
    public AgentReportDto? Report { get; set; }
    public List<Report> History { get; set; } = new();
    public List<AgentEvent> Events { get; set; } = new();
    public int OfflineThreshold { get; set; } = 300;

    // Поля редактирования
    [BindProperty] public string Name { get; set; } = "";
    [BindProperty] public string? PhysicalAddress { get; set; }
    [BindProperty] public string? IpAddress { get; set; }
    [BindProperty] public string? Description { get; set; }
    public string? Message { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        await Load();
        if (Server is null) return NotFound();
        Name = Server.Name;
        PhysicalAddress = Server.PhysicalAddress;
        IpAddress = Server.IpAddress;
        Description = Server.Description;
        return Page();
    }

    private async Task Load()
    {
        OfflineThreshold = (await _db.Settings.FirstOrDefaultAsync())?.OfflineThresholdSeconds ?? 300;
        Server = await _db.Servers.Include(s => s.Client).FirstOrDefaultAsync(s => s.Id == Id);
        if (Server is null) return;
        if (Server.LastReportJson is not null)
        {
            try { Report = JsonSerializer.Deserialize<AgentReportDto>(Server.LastReportJson); } catch { }
        }
        History = await _db.Reports.Where(r => r.ServerId == Id)
            .OrderByDescending(r => r.ReceivedAt).Take(15).ToListAsync();
        Events = await _db.Events.Where(e => e.ServerId == Id)
            .OrderByDescending(e => e.TimeCreated).Take(50).ToListAsync();
    }

    public async Task<IActionResult> OnPostSaveAsync()
    {
        if (!User.IsInRole("Admin")) return Forbid();
        var s = await _db.Servers.FindAsync(Id);
        if (s is null) return NotFound();
        if (!string.IsNullOrWhiteSpace(Name)) s.Name = Name.Trim();
        s.PhysicalAddress = PhysicalAddress?.Trim();
        s.IpAddress = IpAddress?.Trim();
        s.Description = Description?.Trim();
        await _db.SaveChangesAsync();
        await Load();
        Message = "Сохранено";
        return Page();
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        if (!User.IsInRole("Admin")) return Forbid();
        var s = await _db.Servers.FindAsync(Id);
        if (s is not null)
        {
            var clientId = s.ClientId;
            _db.Servers.Remove(s);
            await _db.SaveChangesAsync();
            return RedirectToPage("/Clients/Details", new { id = clientId });
        }
        return RedirectToPage("/Index");
    }

    public async Task<IActionResult> OnPostRegenKeyAsync()
    {
        if (!User.IsInRole("Admin")) return Forbid();
        var s = await _db.Servers.FindAsync(Id);
        if (s is not null)
        {
            s.ApiKey = Services.PasswordHasher.NewApiKey();
            await _db.SaveChangesAsync();
        }
        return RedirectToPage(new { id = Id });
    }
}
