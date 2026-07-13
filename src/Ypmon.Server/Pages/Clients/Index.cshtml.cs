using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Ypmon.Server.Data;
using Ypmon.Server.Services;

namespace Ypmon.Server.Pages.Clients;

public class IndexModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly TelegramReportService _report;
    public IndexModel(AppDbContext db, TelegramReportService report) { _db = db; _report = report; }

    public List<Client> Clients { get; set; } = new();

    [BindProperty] public string NewClientName { get; set; } = "";
    [BindProperty] public string? NewClientDescription { get; set; }

    public async Task OnGetAsync()
    {
        Clients = await _db.Clients.Include(c => c.Servers).OrderBy(c => c.Name).ToListAsync();
    }

    public async Task<IActionResult> OnPostAddAsync()
    {
        if (!User.CanEdit()) return Forbid();
        if (!string.IsNullOrWhiteSpace(NewClientName))
        {
            var client = new Client { Name = NewClientName.Trim(), Description = NewClientDescription?.Trim() };
            _db.Clients.Add(client);
            await _db.SaveChangesAsync();

            // Создаём тему в Telegram-группе для этого клиента (если бот настроен).
            var settings = await _db.Settings.FirstOrDefaultAsync();
            if (settings is not null && settings.TelegramEnabled)
            {
                try { await _report.EnsureTopicAsync(settings, client); } catch { }
            }
        }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        if (!User.CanEdit()) return Forbid();
        var c = await _db.Clients.FindAsync(id);
        if (c is not null) { _db.Clients.Remove(c); await _db.SaveChangesAsync(); }
        return RedirectToPage();
    }
}
