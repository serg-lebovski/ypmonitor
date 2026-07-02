using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Ypmon.Server.Data;

namespace Ypmon.Server.Pages;

public class ErrorsModel : PageModel
{
    private readonly AppDbContext _db;
    public ErrorsModel(AppDbContext db) => _db = db;

    public record Row(long Id, int ServerId, string ServerName, string ClientName,
        DateTimeOffset TimeCreated, string Level, string LogName, string Source, int EventId, string Message);

    public List<Row> Items { get; set; } = new();
    public int Total { get; set; }

    [BindProperty(SupportsGet = true)] public int? ServerId { get; set; }
    [BindProperty(SupportsGet = true)] public string? Level { get; set; }

    public List<(int Id, string Name)> ServerOptions { get; set; } = new();

    public async Task OnGetAsync()
    {
        ServerOptions = (await _db.Servers.Include(s => s.Client).OrderBy(s => s.Name).ToListAsync())
            .Select(s => (s.Id, (s.Client != null ? s.Client.Name + " / " : "") + s.Name)).ToList();

        var q = _db.Events.Include(e => e.Server).ThenInclude(s => s!.Client).AsQueryable();
        if (ServerId is int sid) q = q.Where(e => e.ServerId == sid);
        if (!string.IsNullOrWhiteSpace(Level)) q = q.Where(e => e.Level == Level);

        Total = await q.CountAsync();
        Items = await q.OrderByDescending(e => e.TimeCreated).Take(200)
            .Select(e => new Row(e.Id, e.ServerId,
                e.Server!.Name,
                e.Server.Client != null ? e.Server.Client.Name : "",
                e.TimeCreated, e.Level, e.LogName, e.Source, e.EventId, e.Message))
            .ToListAsync();
    }
}
