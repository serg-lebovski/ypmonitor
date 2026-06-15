using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Ypmon.Server.Data;
using Ypmon.Shared;

namespace Ypmon.Server.Pages;

public class IndexModel : PageModel
{
    private readonly AppDbContext _db;
    public IndexModel(AppDbContext db) => _db = db;

    public List<Client> Clients { get; set; } = new();
    public int OfflineThreshold { get; set; } = 300;

    [BindProperty(SupportsGet = true)] public int? Server { get; set; }
    public MonitoredServer? Selected { get; set; }
    public AgentReportDto? SelectedReport { get; set; }

    public int TotalServers { get; set; }
    public int OkCount { get; set; }
    public int ProblemCount { get; set; }
    public int OfflineCount { get; set; }

    public async Task OnGetAsync()
    {
        var settings = await _db.Settings.FirstOrDefaultAsync();
        OfflineThreshold = settings?.OfflineThresholdSeconds ?? 300;

        Clients = await _db.Clients
            .Include(c => c.Servers)
            .OrderBy(c => c.Name)
            .ToListAsync();

        foreach (var c in Clients)
            c.Servers = c.Servers.OrderBy(s => s.Name).ToList();

        var allServers = Clients.SelectMany(c => c.Servers).ToList();
        TotalServers = allServers.Count;
        foreach (var s in allServers)
        {
            if (s.IsOffline(OfflineThreshold)) OfflineCount++;
            else if (s.LastOutcome == JobOutcome.Ok) OkCount++;
            else if (s.LastOutcome >= JobOutcome.Warning) ProblemCount++;
        }

        if (Server is not null)
        {
            Selected = allServers.FirstOrDefault(s => s.Id == Server);
            if (Selected?.LastReportJson is not null)
            {
                try { SelectedReport = JsonSerializer.Deserialize<AgentReportDto>(Selected.LastReportJson); }
                catch { }
            }
        }
    }
}
