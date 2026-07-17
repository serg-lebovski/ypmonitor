using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ypmon.Server.Data;
using Ypmon.Shared;

namespace Ypmon.Server.Services;

/// <summary>Обработка входящего отчёта агента: статусы папок, ошибки Windows, оповещения.</summary>
public class ReportIngestService
{
    private readonly AppDbContext _db;
    private readonly AlertService _alerts;
    private readonly RemoteCommandService _commands;   // [YPMON-REMOTE-CMD]
    private readonly ILogger<ReportIngestService> _log;

    public ReportIngestService(AppDbContext db, AlertService alerts, RemoteCommandService commands, ILogger<ReportIngestService> log)
    {
        _db = db; _alerts = alerts; _commands = commands; _log = log;
    }

    public async Task<ReportAckDto> IngestAsync(string apiKey, AgentReportDto report)
    {
        var server = await _db.Servers.Include(s => s.Client)
            .FirstOrDefaultAsync(s => s.ApiKey == apiKey);

        if (server is null)
            return new ReportAckDto { Accepted = false, Message = "Неизвестный API-ключ" };

        // Интервалы агента (настраиваются в агенте) — по heartbeat-интервалу считается порог «офлайн».
        if (report.HeartbeatIntervalSeconds > 0) server.LastHeartbeatIntervalSeconds = report.HeartbeatIntervalSeconds;
        if (report.ReportIntervalSeconds > 0) server.LastReportIntervalSeconds = report.ReportIntervalSeconds;

        // Heartbeat: время связи + свежие данные о дисках.
        if (report.IsHeartbeat)
        {
            server.LastSeenAt = DateTimeOffset.UtcNow;
            server.LastReportedAt = report.ReportedAt;
            if (report.Disks.Count > 0)
                server.LastDisksJson = JsonSerializer.Serialize(report.Disks);
            var ack = Ack(server, "OK (heartbeat)");
            _commands.FillAck(server, ack);   // [YPMON-REMOTE-CMD] флаги «нужен отчёт» / «обновись»
            await _db.SaveChangesAsync();
            return ack;
        }

        var settings = await _db.Settings.FirstOrDefaultAsync();
        var prevOutcome = server.LastOutcome;

        // Порог устаревания: у сервера, иначе глобальный по умолчанию.
        var staleDays = server.BackupStaleDays > 0
            ? server.BackupStaleDays
            : (settings?.DefaultBackupStaleDays ?? 1);

        // Вычисляем статус каждой папки.
        foreach (var f in report.Folders)
            f.Outcome = EvaluateFolder(f, staleDays);

        var overall = report.Folders.Count == 0
            ? JobOutcome.Unknown
            : report.Folders.Max(f => f.Outcome);

        // Фильтрация игнорируемых ошибок Windows.
        var ignore = IgnoreRules.Parse(settings?.WindowsErrorIgnore);
        var events = report.EventLogErrors.Where(e => !ignore.IsIgnored(e)).ToList();
        report.EventLogErrors = events;

        // История + кэш состояния.
        _db.Reports.Add(new Report
        {
            ServerId = server.Id,
            ReceivedAt = DateTimeOffset.UtcNow,
            ReportedAt = report.ReportedAt,
            ServerAvailable = true,
            Outcome = overall,
            BackupCount = report.Folders.Sum(f => f.FileCount),
            MachineName = report.MachineName,
            PayloadJson = JsonSerializer.Serialize(report)
        });

        server.LastSeenAt = DateTimeOffset.UtcNow;
        server.LastReportedAt = report.ReportedAt;
        server.LastOutcome = overall;
        server.LastServerAvailable = true;
        server.LastBackupCount = report.Folders.Sum(f => f.FileCount);
        server.LastMachineName = report.MachineName;
        server.LastAgentVersion = report.AgentVersion;
        server.LastReportJson = JsonSerializer.Serialize(report);
        if (report.Disks.Count > 0)
            server.LastDisksJson = JsonSerializer.Serialize(report.Disks);
        if (report.PhysicalDisks.Count > 0)
            server.LastPhysicalDisksJson = JsonSerializer.Serialize(report.PhysicalDisks);
        _commands.MarkReportReceived(server);   // [YPMON-REMOTE-CMD] полный отчёт получен — запрос выполнен

        // Сохраняем новые ошибки Windows (без дублей).
        if (events.Count > 0)
        {
            var ordered = events.Where(e => e.TimeCreated != default).OrderBy(e => e.TimeCreated).ToList();
            if (ordered.Count > 0)
            {
                var minT = ordered.First().TimeCreated;
                var maxT = ordered.Last().TimeCreated;
                var existing = await _db.Events
                    .Where(e => e.ServerId == server.Id && e.TimeCreated >= minT && e.TimeCreated <= maxT)
                    .Select(e => new { e.TimeCreated, e.EventId, e.Source })
                    .ToListAsync();
                var seen = new HashSet<string>(existing.Select(x => $"{x.TimeCreated:O}|{x.EventId}|{x.Source}"));
                foreach (var e in ordered)
                {
                    var key = $"{e.TimeCreated:O}|{e.EventId}|{e.Source}";
                    if (!seen.Add(key)) continue;
                    _db.Events.Add(new AgentEvent
                    {
                        ServerId = server.Id,
                        ReceivedAt = DateTimeOffset.UtcNow,
                        TimeCreated = e.TimeCreated,
                        LogName = e.LogName ?? "",
                        Source = e.Source ?? "",
                        Level = e.Level ?? "",
                        EventId = e.EventId,
                        Message = e.Message ?? ""
                    });
                }
            }
        }

        // Контроль резкого уменьшения объёма бэкапа (> 20% от прошлого) — только по доступным папкам,
        // чтобы временная недоступность сетевой шары не поднимала ложную тревогу.
        var currentSize = report.Folders.Where(f => f.Accessible).Sum(f => f.TotalSizeBytes);
        var shrinkAlarm = false;
        if (!server.BackupShrinkActive)
        {
            if (server.PrevBackupSizeBytes > 0 && currentSize > 0 &&
                currentSize < server.PrevBackupSizeBytes * 0.8)
            {
                server.BackupShrinkActive = true;
                server.BackupShrinkFromBytes = server.PrevBackupSizeBytes;
                server.BackupShrinkToBytes = currentSize;
                server.BackupShrinkAt = DateTimeOffset.UtcNow;
                shrinkAlarm = true;
                // Опорный объём НЕ обновляем — держим до подтверждения администратором.
            }
            else if (currentSize > 0)
            {
                server.PrevBackupSizeBytes = currentSize;   // нормальный ход — двигаем опорную точку
            }
        }
        // Если тревога уже активна — опорный объём заморожен до подтверждения.

        await _db.SaveChangesAsync();

        // Оповещение при переходе папок в проблемное состояние (архивация — отдельно от Windows-ошибок).
        if (settings is not null && overall >= JobOutcome.Warning && prevOutcome < JobOutcome.Warning)
        {
            var problems = report.Folders.Where(f => f.Outcome >= JobOutcome.Warning)
                .Select(f => $"• {f.Name}: {f.Message}").ToList();
            await _alerts.SendAsync(settings,
                $"YPMon: проблема с бэкапами — {server.Client?.Name} / {server.Name}",
                $"Машина: {report.MachineName}\n" + string.Join("\n", problems));
        }

        // Оповещение о резком уменьшении объёма бэкапа.
        if (settings is not null && shrinkAlarm)
        {
            var pct = server.BackupShrinkFromBytes > 0
                ? (1 - (double)server.BackupShrinkToBytes / server.BackupShrinkFromBytes) * 100 : 0;
            await _alerts.SendAsync(settings,
                $"YPMon: объём бэкапа резко уменьшился — {server.Client?.Name} / {server.Name}",
                $"Было {UiHelpers.Bytes(server.BackupShrinkFromBytes)}, стало {UiHelpers.Bytes(server.BackupShrinkToBytes)} " +
                $"(−{pct:0}%). Проверьте, всё ли верно. Если архив действительно стал меньше — подтвердите на странице сервера.");
        }

        return Ack(server, "OK");
    }

    private static JobOutcome EvaluateFolder(FolderStatusDto f, int staleDays)
    {
        if (!f.Accessible) return JobOutcome.Error;
        if (f.FileCount == 0) return JobOutcome.Error;
        if (staleDays > 0 && f.LastBackupAt is { } last &&
            (DateTimeOffset.UtcNow - last).TotalDays > staleDays)
            return JobOutcome.Error;
        return JobOutcome.Ok;
    }

    private static ReportAckDto Ack(MonitoredServer s, string msg) => new()
    {
        Accepted = true,
        Message = msg,
        ClientName = s.Client?.Name,
        ServerName = s.Name
    };
}

/// <summary>Правила игнорирования ошибок Windows (код события или подстрока источника).</summary>
public sealed class IgnoreRules
{
    private readonly HashSet<int> _eventIds = new();
    private readonly List<string> _sources = new();

    public static IgnoreRules Parse(string? text)
    {
        var r = new IgnoreRules();
        if (string.IsNullOrWhiteSpace(text)) return r;
        foreach (var raw in text.Split(new[] { '\n', '\r', ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (int.TryParse(raw, out var id)) r._eventIds.Add(id);
            else r._sources.Add(raw);
        }
        return r;
    }

    public bool IsIgnored(EventLogEntryDto e)
    {
        if (_eventIds.Contains(e.EventId)) return true;
        foreach (var s in _sources)
            if (!string.IsNullOrEmpty(e.Source) && e.Source.Contains(s, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}
