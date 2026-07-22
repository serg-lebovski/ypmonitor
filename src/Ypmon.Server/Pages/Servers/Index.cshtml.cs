using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Ypmon.Server.Data;
using Ypmon.Server.Services;
using Ypmon.Shared;

namespace Ypmon.Server.Pages.Servers;

/// <summary>Список всех серверов (сгруппирован по клиентам) с краткой сводкой. Клик по серверу → настройка.</summary>
public class IndexModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly RemoteCommandService _commands;
    private readonly AuditService _audit;

    public IndexModel(AppDbContext db, RemoteCommandService commands, AuditService audit)
    {
        _db = db; _commands = commands; _audit = audit;
    }

    public List<Client> Clients { get; set; } = new();
    public int OfflineThreshold { get; set; } = 300;
    public int DefaultBackupStaleDays { get; set; } = 1;
    public string? Message { get; set; }

    // Предразобранные данные из JSON последнего отчёта (чтобы не парсить в разметке).
    public Dictionary<int, List<DiskStatusDto>> DisksByServer { get; } = new();
    public Dictionary<int, List<FolderStatusDto>> FoldersByServer { get; } = new();
    public Dictionary<int, Dictionary<string, int>> ThresholdsByServer { get; } = new();

    public async Task OnGetAsync() => await Load();

    private async Task Load()
    {
        var settings = await _db.Settings.FirstOrDefaultAsync();
        OfflineThreshold = settings?.OfflineThresholdSeconds ?? 300;
        DefaultBackupStaleDays = settings?.DefaultBackupStaleDays ?? 1;

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
                ThresholdsByServer[s.Id] = AvailabilityMonitor.ParseThresholds(s.DiskAlertsJson);
            }
        }
    }

    /// <summary>Сводка проблемных папок сервера для подсказки на бейдже статуса.</summary>
    public string ProblemSummary(MonitoredServer s)
    {
        if (!FoldersByServer.TryGetValue(s.Id, out var folders)) return UiHelpers.OutcomeText(s.LastOutcome);
        var stale = UiHelpers.EffectiveStaleDays(s.BackupStaleDays, DefaultBackupStaleDays);
        var lines = folders.Where(f => f.Outcome != JobOutcome.Ok)
                           .Select(f => $"{f.Name}: {UiHelpers.FolderReason(f, stale)}")
                           .ToList();
        return lines.Count > 0 ? string.Join("\n", lines) : UiHelpers.OutcomeText(s.LastOutcome);
    }

    /// <summary>Кнопка «Обновить все агенты»: ставит всем серверам команду на обновление.</summary>
    public async Task<IActionResult> OnPostUpdateAllAsync()
    {
        if (!User.CanEdit()) return Forbid();

        var ids = await _db.Servers.Select(s => s.Id).ToListAsync();
        var user = User.Identity?.Name ?? "?";
        foreach (var id in ids)
            await _commands.IssueAsync(id, AgentCommand.UpdateAgent, user);

        await _audit.LogAsync(User, "Команда всем агентам: обновиться", $"серверов: {ids.Count}");
        await Load();
        Message = $"Команда на обновление отправлена всем агентам ({ids.Count}). Каждый обновится при следующей связи. " +
                  "Агенты офлайн получат её при возврате, если успеют в течение 10 минут; остальные обновятся сами по расписанию.";
        return Page();
    }
}
