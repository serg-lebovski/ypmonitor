using Microsoft.EntityFrameworkCore;
using Ypmon.Server.Data;
using Ypmon.Shared;

namespace Ypmon.Server.Services;

/// <summary>Фоновая служба: детект офлайн-серверов, оповещения, чистка истории.</summary>
public class MaintenanceService : BackgroundService
{
    private readonly IServiceProvider _sp;
    private readonly ILogger<MaintenanceService> _log;
    private readonly HashSet<int> _offlineNotified = new();

    public MaintenanceService(IServiceProvider sp, ILogger<MaintenanceService> log)
    {
        _sp = sp;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Небольшая задержка, чтобы БД успела проинициализироваться.
        try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); } catch { }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickAsync(stoppingToken); }
            catch (Exception ex) { _log.LogError(ex, "Ошибка в обслуживающей службе"); }

            try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); }
            catch { }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var alerts = scope.ServiceProvider.GetRequiredService<AlertService>();

        var settings = await db.Settings.FirstOrDefaultAsync(ct);
        if (settings is null) return;

        var servers = await db.Servers.Include(s => s.Client).ToListAsync(ct);
        foreach (var s in servers)
        {
            var offline = s.IsOffline(settings.OfflineThresholdSeconds);
            if (offline && s.LastSeenAt is not null && !_offlineNotified.Contains(s.Id))
            {
                _offlineNotified.Add(s.Id);
                await alerts.SendAsync(settings,
                    $"YPMon: агент офлайн — {s.Client?.Name} / {s.Name}",
                    $"Последний отчёт: {s.LastSeenAt:yyyy-MM-dd HH:mm:ss} UTC. Агент перестал выходить на связь.");
            }
            else if (!offline)
            {
                _offlineNotified.Remove(s.Id);
            }
        }

        // Чистка старой истории
        if (settings.ReportRetentionDays > 0)
        {
            var cutoff = DateTimeOffset.UtcNow.AddDays(-settings.ReportRetentionDays);
            var old = db.Reports.Where(r => r.ReceivedAt < cutoff);
            db.Reports.RemoveRange(old);
            await db.SaveChangesAsync(ct);
        }
    }
}
