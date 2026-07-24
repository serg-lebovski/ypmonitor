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

    /// <summary>Разбивка «С проблемами» по причине — для подсказки в плитке дашборда.</summary>
    public int ArchiveProblemCount { get; set; }

    /// <summary>Диски: SMART (здоровье/перегрев) или заполнение ниже порога.</summary>
    public int DiskProblemCount { get; set; }

    private readonly HashSet<int> _diskFillProblemIds = new();

    // Место на дисках: список серверов (как в таблице серверов), у каждого — кружок на диск.
    public List<DiskServerRow> DiskServers { get; set; } = new();

    /// <summary>Сколько серверов с проблемой по месту (есть диск ниже порога) — для свёрнутого заголовка.</summary>
    public int DiskProblemServers { get; set; }

    // График «работа архивации»: итоги полных отчётов по дням (последние 14 дней).
    public List<DaySlice> ArchiveDays { get; set; } = new();
    public int ArchiveMaxPerDay { get; set; } = 1;

    public record DiskDot(string Disk, string? Label, double FreePercent, int Threshold, string Sev);

    public record DiskServerRow(int ServerId, string Server, string Client, List<DiskDot> Disks, double WorstFree, bool Problem);

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
        ArchiveProblemCount = ProblemCount;

        BuildDiskServers(allServers);   // заполняет DiskServers и _diskFillProblemIds (заполнение ниже порога)
        // Диски: заполнение ниже порога ИЛИ проблема SMART (здоровье/перегрев) — не считая офлайн,
        // как и остальные метрики в этой строке.
        DiskProblemCount = allServers.Count(s => !s.IsOffline(OfflineThreshold)
            && (s.AlertDiskHealthActive || _diskFillProblemIds.Contains(s.Id)));

        await BuildArchiveDaysAsync();
    }

    private void BuildDiskServers(List<MonitoredServer> servers)
    {
        var rows = new List<DiskServerRow>();
        foreach (var s in servers)
        {
            if (string.IsNullOrWhiteSpace(s.LastDisksJson)) continue;
            List<DiskStatusDto> disks;
            try { disks = JsonSerializer.Deserialize<List<DiskStatusDto>>(s.LastDisksJson) ?? new(); }
            catch { continue; }
            var thresholds = AvailabilityMonitor.ParseThresholds(s.DiskAlertsJson);
            var dots = new List<DiskDot>();
            foreach (var d in disks)
            {
                if (d.TotalBytes <= 0) continue;
                // Порог свободного места: настроенный для диска, иначе значение по умолчанию.
                var th = thresholds.TryGetValue(d.Name, out var t) ? t : AvailabilityMonitor.DefaultDiskFreePercent;
                var free = d.FreePercent;
                var sev = th > 0 && free < th ? "err"
                    : th > 0 && free < th * 1.5 ? "warn"
                    : "ok";
                dots.Add(new DiskDot(d.Name, d.Label, free, th, sev));
            }
            if (dots.Count == 0) continue;
            var problem = dots.Any(x => x.Sev == "err");
            if (problem) _diskFillProblemIds.Add(s.Id);
            rows.Add(new DiskServerRow(s.Id, s.Name, s.Client?.Name ?? "", dots, dots.Min(x => x.FreePercent), problem));
        }
        DiskProblemServers = rows.Count(x => x.Problem);
        // Худшие (меньше всего свободного места на самом заполненном диске) — сверху.
        DiskServers = rows.OrderBy(x => x.WorstFree).ToList();
    }

    private async Task BuildArchiveDaysAsync()
    {
        var since = DateTimeOffset.UtcNow.Date.AddDays(-13);   // 14 дней включая сегодня
        // Фильтр по дате — в памяти: sqlite не транслирует сравнение DateTimeOffset в WHERE.
        // Тянем только два столбца; таблицу ограничивает срок хранения истории.
        var rows = (await _db.Reports
                .Select(r => new { r.ReceivedAt, r.Outcome })
                .ToListAsync())
            .Where(r => r.ReceivedAt >= since)
            .ToList();

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
