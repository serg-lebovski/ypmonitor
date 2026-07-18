using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Ypmon.Agent.Services;

/// <summary>
/// Простой файловый логгер: пишет в папку <c>log</c> рядом с exe, отдельный файл на день
/// (<c>agent-ГГГГ-ММ-ДД.log</c>). Файлы старше 30 дней удаляются автоматически (при старте и при
/// смене суток). Уровень — Information и выше.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);
    private readonly string _dir = Path.Combine(AppContext.BaseDirectory, "log");
    private readonly object _lock = new();
    private DateTime _lastPruneDay = DateTime.MinValue;

    public FileLoggerProvider()
    {
        try { Directory.CreateDirectory(_dir); Prune(); _lastPruneDay = DateTime.Now.Date; } catch { }
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    internal void Write(string category, LogLevel level, string message, Exception? ex)
    {
        try
        {
            var now = DateTimeOffset.Now;
            var line = $"{now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {Short(category)}: {message}";
            if (ex is not null) line += Environment.NewLine + ex;
            var file = Path.Combine(_dir, $"agent-{now:yyyy-MM-dd}.log");
            lock (_lock)
            {
                if (now.Date != _lastPruneDay) { Prune(); _lastPruneDay = now.Date; }   // чистим при смене суток
                File.AppendAllText(file, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch { /* логирование не должно ронять агента */ }
    }

    private static string Short(string category)
    {
        var i = category.LastIndexOf('.');
        return i >= 0 && i < category.Length - 1 ? category[(i + 1)..] : category;
    }

    private void Prune()
    {
        try
        {
            if (!Directory.Exists(_dir)) return;
            var cutoff = DateTime.Now.Date - MaxAge;
            foreach (var f in Directory.GetFiles(_dir, "agent-*.log"))
            {
                var stem = Path.GetFileNameWithoutExtension(f);           // agent-2026-07-18
                var datePart = stem.Length > 6 ? stem[6..] : "";
                DateTime ts;
                if (!DateTime.TryParseExact(datePart, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out ts))
                {
                    try { ts = File.GetLastWriteTime(f); } catch { continue; }
                }
                if (ts.Date < cutoff) { try { File.Delete(f); } catch { } }
            }
        }
        catch { }
    }

    public void Dispose() { }
}

internal sealed class FileLogger : ILogger
{
    private readonly FileLoggerProvider _provider;
    private readonly string _category;

    public FileLogger(FileLoggerProvider provider, string category)
    {
        _provider = provider; _category = category;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel level) => level >= LogLevel.Information;

    public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? ex,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(level)) return;
        _provider.Write(_category, level, formatter(state, ex), ex);
    }
}
