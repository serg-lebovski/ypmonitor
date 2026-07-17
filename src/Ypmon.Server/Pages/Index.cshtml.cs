using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Ypmon.Server.Data;
using Ypmon.Server.Services;
using Ypmon.Shared;

namespace Ypmon.Server.Pages;

public class IndexModel : PageModel
{
    private readonly AppDbContext _db;
    public IndexModel(AppDbContext db) => _db = db;

    public List<Client> Clients { get; set; } = new();
    public int OfflineThreshold { get; set; } = 300;

    public int TotalServers { get; set; }
    public int OkCount { get; set; }
    public int ProblemCount { get; set; }
    public int OfflineCount { get; set; }

    // График «место на дисках»: по одной полосе на диск, худшие сверху.
    public List<DiskBar> DiskBars { get; set; } = new();

    // График «работа архивации»: итоги полных отчётов по дням (последние 14 дней).
    public List<DaySlice> ArchiveDays { get; set; } = new();
    public int ArchiveMaxPerDay { get; set; } = 1;

    public record DiskBar(int ServerId, string Server, string Client, string Disk, string? Label,
        double FillPercent, double FreePercent, int Threshold, string Sev);

    public record DaySlice(string Label, int Ok, int Warning, int Error)
    {
        public int Total => Ok + Warning + Error;
    }

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
            else if (s.LastOutcome == JobOutcome.Ok && !s.BackupShrinkActive) OkCount++;
            else if (s.LastOutcome >= JobOutcome.Warning || s.BackupShrinkActive) ProblemCount++;
        }

        BuildDiskBars(allServers);
        await BuildArchiveDaysAsync();
    }

    private void BuildDiskBars(List<MonitoredServer> servers)
    {
        var bars = new List<DiskBar>();
        foreach (var s in servers)
        {
            if (string.IsNullOrWhiteSpace(s.LastDisksJson)) continue;
            List<DiskStatusDto> disks;
            try { disks = JsonSerializer.Deserialize<List<DiskStatusDto>>(s.LastDisksJson) ?? new(); }
            catch { continue; }
            var thresholds = AvailabilityMonitor.ParseThresholds(s.DiskAlertsJson);
            foreach (var d in disks)
            {
                if (d.TotalBytes <= 0) continue;
                // Порог свободного места: настроенный для диска, иначе значение по умолчанию.
                var th = thresholds.TryGetValue(d.Name, out var t) ? t : AvailabilityMonitor.DefaultDiskFreePercent;
                var free = d.FreePercent;
                var sev = th > 0 && free < th ? "err"
                    : th > 0 && free < th * 1.5 ? "warn"
                    : "ok";
                bars.Add(new DiskBar(s.Id, s.Name, s.Client?.Name ?? "", d.Name, d.Label,
                    100 - free, free, th, sev));
            }
        }
        // Худшие (меньше всего свободного места) — сверху; ограничим топ-16, чтобы график читался.
        DiskBars = bars.OrderBy(b => b.FreePercent).Take(16).ToList();
    }

    private async Task BuildArchiveDaysAsync()
    {
        var since = DateTimeOffset.UtcNow.Date.AddDays(-13);   // 14 дней включая сегодня
        var rows = await _db.Reports
            .Where(r => r.ReceivedAt >= since)
            .Select(r => new { r.ReceivedAt, r.Outcome })
            .ToListAsync();

        int max = 1;
        for (int i = 0; i < 14; i++)
        {
            var day = since.AddDays(i);
            var dayRows = rows.Where(r => r.ReceivedAt.UtcDateTime.Date == day).ToList();
            var ok = dayRows.Count(r => r.Outcome == JobOutcome.Ok);
            var warn = dayRows.Count(r => r.Outcome == JobOutcome.Warning);
            var err = dayRows.Count(r => r.Outcome == JobOutcome.Error);
            max = Math.Max(max, ok + warn + err);
            ArchiveDays.Add(new DaySlice(day.ToString("dd.MM"), ok, warn, err));
        }
        ArchiveMaxPerDay = max;
    }

}
