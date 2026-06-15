using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ypmon.Server.Data;
using Ypmon.Shared;

namespace Ypmon.Server.Services;

/// <summary>Обработка входящего отчёта агента: сохранение, обновление статуса, оповещения.</summary>
public class ReportIngestService
{
    private readonly AppDbContext _db;
    private readonly AlertService _alerts;
    private readonly ILogger<ReportIngestService> _log;

    public ReportIngestService(AppDbContext db, AlertService alerts, ILogger<ReportIngestService> log)
    {
        _db = db;
        _alerts = alerts;
        _log = log;
    }

    public async Task<ReportAckDto> IngestAsync(string apiKey, AgentReportDto report)
    {
        var server = await _db.Servers.Include(s => s.Client)
            .FirstOrDefaultAsync(s => s.ApiKey == apiKey);

        if (server is null)
            return new ReportAckDto { Accepted = false, Message = "Неизвестный API-ключ" };

        var prevOutcome = server.LastOutcome;
        var prevSeen = server.LastSeenAt;

        // Сохраняем историю
        _db.Reports.Add(new Report
        {
            ServerId = server.Id,
            ReceivedAt = DateTimeOffset.UtcNow,
            ReportedAt = report.ReportedAt,
            ServerAvailable = report.ServerAvailable,
            Outcome = report.OverallOutcome,
            BackupCount = report.TotalBackupCount,
            MachineName = report.MachineName,
            PayloadJson = JsonSerializer.Serialize(report)
        });

        // Обновляем кэш состояния
        server.LastSeenAt = DateTimeOffset.UtcNow;
        server.LastReportedAt = report.ReportedAt;
        server.LastOutcome = report.OverallOutcome;
        server.LastServerAvailable = report.ServerAvailable;
        server.LastBackupCount = report.TotalBackupCount;
        server.LastMachineName = report.MachineName;
        server.LastAgentVersion = report.AgentVersion;
        server.LastReportJson = JsonSerializer.Serialize(report);

        await _db.SaveChangesAsync();

        // Оповещение при переходе в проблемное состояние
        var settings = await _db.Settings.FirstOrDefaultAsync();
        if (settings is not null && report.OverallOutcome >= JobOutcome.Warning && prevOutcome < JobOutcome.Warning)
        {
            var failing = report.Jobs.Where(j => j.Outcome >= JobOutcome.Warning)
                .Select(j => $"• {j.Name}: {j.Message}").ToList();
            var avail = report.ServerAvailable ? "" : $"\nСервер БД недоступен: {report.AvailabilityMessage}";
            await _alerts.SendAsync(settings,
                $"YPMon: проблема на {server.Client?.Name} / {server.Name}",
                $"Машина: {report.MachineName}{avail}\n" + string.Join("\n", failing));
        }

        return new ReportAckDto
        {
            Accepted = true,
            Message = "OK",
            ClientName = server.Client?.Name,
            ServerName = server.Name
        };
    }
}
