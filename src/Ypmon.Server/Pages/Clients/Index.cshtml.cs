using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Ypmon.Server.Data;

namespace Ypmon.Server.Pages.Clients;

public class IndexModel : PageModel
{
    private readonly AppDbContext _db;
    public IndexModel(AppDbContext db) => _db = db;

    public List<Client> Clients { get; set; } = new();

    [BindProperty] public string NewClientName { get; set; } = "";
    [BindProperty] public string? NewClientDescription { get; set; }

    public async Task OnGetAsync()
    {
        Clients = await _db.Clients.Include(c => c.Servers).OrderBy(c => c.Name).ToListAsync();
    }

    public async Task<IActionResult> OnPostAddAsync()
    {
        if (!string.IsNullOrWhiteSpace(NewClientName))
        {
            _db.Clients.Add(new Client { Name = NewClientName.Trim(), Description = NewClientDescription?.Trim() });
            await _db.SaveChangesAsync();
        }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        var c = await _db.Clients.FindAsync(id);
        if (c is not null) { _db.Clients.Remove(c); await _db.SaveChangesAsync(); }
        return RedirectToPage();
    }
}
