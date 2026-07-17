using System.Text.Json;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Ypmon.Server.Data;
using Ypmon.Shared;

namespace Ypmon.Server.Pages.Servers;

/// <summary>Список всех серверов (сгруппирован по клиентам) с краткой сводкой. Клик по серверу → настройка.</summary>
public class IndexModel : PageModel
{
    private readonly AppDbContext _db;
    public IndexModel(AppDbContext db) => _db = db;

    public List<Client> Clients { get; set; } = new();
    public int OfflineThreshold { get; set; } = 300;

    // Предразобранные данные из JSON последнего отчёта (чтобы не парсить в разметке).
    public Dictionary<int, List<DiskStatusDto>> DisksByServer { get; } = new();
    public Dictionary<int, List<FolderStatusDto>> FoldersByServer { get; } = new();
    public Dictionary<int, Dictionary<string, int>> ThresholdsByServer { get; } = new();

    public async Task OnGetAsync()
    {
        var settings = await _db.Settings.FirstOrDefaultAsync();
        OfflineThreshold = settings?.OfflineThresholdSeconds ?? 300;

        Clients = await _db.Clients.Include(c => c.Servers).OrderBy(c => c.Name).ToListAsync();
        foreach (var c in Clients)
        {
            c.Servers = c.Servers.OrderBy(s => s.Name).ToList();
            foreach (var s in c.Servers)
            {
                if (!string.IsNullOrWhiteSpace(s.LastDisksJson))
                    try { DisksByServer[s.Id] = JsonSerializer.Deserialize<List<DiskStatusDto>>(s.LastDisksJson!) ?? new(); } catch { }
                if (!string.IsNullOrWhiteSpace(s.LastReportJson))
                    try { FoldersByServer[s.Id] = JsonSerializer.Deserialize<AgentReportDto>(s.LastReportJson!)?.Folders ?? new(); } catch { }
                ThresholdsByServer[s.Id] = Services.AvailabilityMonitor.ParseThresholds(s.DiskAlertsJson);
            }
        }
    }
}
