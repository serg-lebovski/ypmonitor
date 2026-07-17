using Microsoft.EntityFrameworkCore;
using Ypmon.Server.Data;

namespace Ypmon.Server.Services;

/// <summary>
/// Фоновая служба: раз в день в заданный час по омскому времени (UTC+6) шлёт отчёт по архивации.
/// Также при старте поднимает WireGuard (если включён) для доступа бота к Telegram.
/// </summary>
public class DailyReportScheduler : BackgroundService
{
    private readonly IServiceProvider _sp;
    private readonly WireGuardProxyService _wg;
    private readonly ILogger<DailyReportScheduler> _log;
    private DateOnly _lastSentOmsk = DateOnly.MinValue;

    public DailyReportScheduler(IServiceProvider sp, WireGuardProxyService wg, ILogger<DailyReportScheduler> log)
    {
        _sp = sp; _wg = wg; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken); } catch { }

        // Поднять WireGuard из сохранённых настроек.
        await ApplyWireGuardAsync();

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickAsync(); }
            catch (Exception ex) { _log.LogError(ex, "Ошибка планировщика отчётов"); }
            try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); } catch { }
        }
    }

    private async Task ApplyWireGuardAsync()
    {
        try
        {
            using var scope = _sp.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var s = await db.Settings.FirstOrDefaultAsync();
            if (s is not null) _wg.Apply(s.WireGuardEnabled, s.WireGuardConfig);
        }
        catch (Exception ex) { _log.LogWarning(ex, "Не удалось применить WireGuard при старте"); }
    }

    private async Task TickAsync()
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var s = await db.Settings.FirstOrDefaultAsync();
        // Отчёт может уходить в Telegram и/или на почту — достаточно включённого расписания.
        if (s is null || !s.DailyReportEnabled) return;

        var omsk = TelegramReportService.OmskNow();
        var today = DateOnly.FromDateTime(omsk);
        var hour = Math.Clamp(s.DailyReportHourOmsk, 0, 23);

        // Отправляем только в течение заданного часа (иначе рестарт после 10:00 слал бы повтор).
        if (omsk.Hour == hour && _lastSentOmsk < today)
        {
            _lastSentOmsk = today;
            var report = scope.ServiceProvider.GetRequiredService<TelegramReportService>();
            var result = await report.SendReportAsync();
            _log.LogInformation("Ежедневный отчёт Telegram: {Result}", result);
        }
    }
}
