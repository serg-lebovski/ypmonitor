using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Ypmon.Server.Data;

namespace Ypmon.Server.Services;

/// <summary>
/// Журнал сервера. Записи складываются в очередь в памяти и пишутся в БД пачками
/// фоновой службой <see cref="ServerLogWriter"/> — чтобы логирование не тормозило
/// приём отчётов и не роняло запрос при недоступной БД.
/// </summary>
public class ServerLogService
{
    /// <summary>Ограниченная очередь: при переполнении теряем самые старые, а не память.</summary>
    private readonly Channel<ServerLog> _queue = Channel.CreateBounded<ServerLog>(
        new BoundedChannelOptions(5000) { FullMode = BoundedChannelFullMode.DropOldest });

    public ChannelReader<ServerLog> Reader => _queue.Reader;

    public void Write(string level, LogArea area, string message,
        string? who = null, string? ip = null, string? details = null)
    {
        _queue.Writer.TryWrite(new ServerLog
        {
            At = DateTimeOffset.UtcNow,
            Level = level,
            Area = area,
            Message = Trim(message, 1000) ?? "",
            Who = Trim(who, 200),
            Ip = Trim(ip, 60),
            Details = Trim(details, 4000),
        });
    }

    public void Info(LogArea area, string message, string? who = null, string? ip = null, string? details = null)
        => Write("Information", area, message, who, ip, details);

    public void Warn(LogArea area, string message, string? who = null, string? ip = null, string? details = null)
        => Write("Warning", area, message, who, ip, details);

    public void Error(LogArea area, string message, string? who = null, string? ip = null, string? details = null)
        => Write("Error", area, message, who, ip, details);

    /// <summary>IP клиента с учётом обратного прокси (X-Forwarded-For).</summary>
    public static string? ClientIp(HttpContext? ctx)
    {
        if (ctx is null) return null;
        var fwd = ctx.Request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(fwd)) return fwd.Split(',')[0].Trim();
        return ctx.Connection.RemoteIpAddress?.ToString();
    }

    private static string? Trim(string? s, int max)
        => string.IsNullOrEmpty(s) ? s : (s.Length <= max ? s : s[..max] + "…");
}

/// <summary>Пишет накопленные записи журнала в БД и раз в сутки чистит старые.</summary>
public class ServerLogWriter : BackgroundService
{
    /// <summary>Сколько дней храним журнал (чистка ежедневная, чтобы не копить месяц разом).</summary>
    public const int RetentionDays = 30;

    private readonly ServerLogService _logs;
    private readonly IServiceProvider _sp;
    private readonly ILogger<ServerLogWriter> _log;
    private DateTime _lastPrune = DateTime.MinValue;

    public ServerLogWriter(ServerLogService logs, IServiceProvider sp, ILogger<ServerLogWriter> log)
    {
        _logs = logs; _sp = sp; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Даём БД проинициализироваться (таблица создаётся при старте).
        try { await Task.Delay(TimeSpan.FromSeconds(12), ct); } catch { return; }

        while (!ct.IsCancellationRequested)
        {
            try { await FlushAsync(ct); }
            catch (Exception ex) { _log.LogError(ex, "Не удалось записать журнал сервера в БД"); }

            try { await Task.Delay(TimeSpan.FromSeconds(5), ct); } catch { break; }
        }
    }

    private async Task FlushAsync(CancellationToken ct)
    {
        var batch = new List<ServerLog>();
        while (batch.Count < 500 && _logs.Reader.TryRead(out var e)) batch.Add(e);

        var needPrune = DateTime.UtcNow - _lastPrune > TimeSpan.FromHours(24);
        if (batch.Count == 0 && !needPrune) return;

        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        if (batch.Count > 0)
        {
            db.Logs.AddRange(batch);
            await db.SaveChangesAsync(ct);
        }

        if (needPrune)
        {
            _lastPrune = DateTime.UtcNow;
            var cutoff = DateTimeOffset.UtcNow.AddDays(-RetentionDays);
            // Через SQL, а не LINQ: сравнение DateTimeOffset EF не умеет транслировать в sqlite.
            var deleted = await db.Database.ExecuteSqlRawAsync(
                @"DELETE FROM ""Logs"" WHERE ""At"" < {0}", new object[] { cutoff }, ct);
            if (deleted > 0) _log.LogInformation("Журнал сервера: удалено старых записей — {N}", deleted);
        }
    }
}
