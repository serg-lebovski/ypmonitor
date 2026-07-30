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
    private readonly IWebHostEnvironment _env;
    private readonly RemoteCommandService _commands;
    private readonly AuditService _audit;
    public IndexModel(AppDbContext db, IWebHostEnvironment env, RemoteCommandService commands, AuditService audit)
    {
        _db = db; _env = env; _commands = commands; _audit = audit;
    }

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

    public record DiskDot(string Disk, string? Label, double FreePercent, int Threshold, string Sev);

    public record DiskServerRow(int ServerId, string Server, string Client, List<DiskDot> Disks, double WorstFree, bool Problem);

    /// <summary>Версия из agent-updates/version.txt — та, что раздаётся серверам как актуальная.</summary>
    public string? LatestAgentVersion { get; set; }

    public record OutdatedAgentRow(int ServerId, string Server, string Client, string Version);
    public List<OutdatedAgentRow> OutdatedAgents { get; set; } = new();

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

        LatestAgentVersion = await ReadLatestAgentVersionAsync();
        if (!string.IsNullOrWhiteSpace(LatestAgentVersion))
            OutdatedAgents = allServers
                .Where(s => !string.IsNullOrWhiteSpace(s.LastAgentVersion) && s.LastAgentVersion != LatestAgentVersion)
                .OrderBy(s => s.Name)
                .Select(s => new OutdatedAgentRow(s.Id, s.Name, s.Client?.Name ?? "", s.LastAgentVersion!))
                .ToList();
    }

    private async Task<string?> ReadLatestAgentVersionAsync()
    {
        var verFile = Path.Combine(_env.ContentRootPath, "agent-updates", "version.txt");
        if (!System.IO.File.Exists(verFile)) return null;
        return (await System.IO.File.ReadAllTextAsync(verFile)).Trim();
    }

    /// <summary>Кнопка «Обновить все»: команда UpdateAgent сразу всем серверам с устаревшей версией.</summary>
    public async Task<IActionResult> OnPostUpdateAllAgentsAsync()
    {
        if (!User.CanEdit()) return Forbid();

        var latest = await ReadLatestAgentVersionAsync();
        if (string.IsNullOrWhiteSpace(latest)) return RedirectToPage();

        var outdated = await _db.Servers
            .Where(s => s.LastAgentVersion != null && s.LastAgentVersion != "" && s.LastAgentVersion != latest)
            .ToListAsync();
        foreach (var s in outdated)
            await _commands.IssueAsync(s.Id, AgentCommand.UpdateAgent, User.Identity?.Name ?? "?");

        if (outdated.Count > 0)
            await _audit.LogAsync(User, "Массовое обновление агентов", $"серверов: {outdated.Count}, до версии {latest}");

        return RedirectToPage();
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

}
