using Ypmon.Server.Data;

namespace Ypmon.Server.Services;

/// <summary>
/// Перехватывает предупреждения и ошибки из обычного ILogger и складывает их
/// в журнал сервера — чтобы администратор видел сбои в интерфейсе, а не только в docker logs.
/// Записи уровня Information не перехватываются: их пишем явно в нужных местах.
/// </summary>
public class DbLoggerProvider : ILoggerProvider
{
    private readonly ServerLogService _logs;

    public DbLoggerProvider(ServerLogService logs) => _logs = logs;

    public ILogger CreateLogger(string categoryName) => new DbLogger(_logs, categoryName);

    public void Dispose() { }

    private class DbLogger : ILogger
    {
        private readonly ServerLogService _logs;
        private readonly string _category;

        public DbLogger(ServerLogService logs, string category) { _logs = logs; _category = category; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel level) => level >= LogLevel.Warning && !IsNoisy(_category);

        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? ex,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(level)) return;

            var message = formatter(state, ex);
            var details = ex is null ? null : $"{_category}\n{ex}";
            _logs.Write(level >= LogLevel.Error ? "Error" : "Warning", AreaFor(_category),
                message, who: null, ip: null, details: details ?? _category);
        }

        /// <summary>Категории, которые нельзя писать в БД: иначе сбой записи вызовет сам себя.</summary>
        private static bool IsNoisy(string c)
            => c.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal)
            || c.StartsWith("Npgsql", StringComparison.Ordinal)
            || c.Contains("ServerLogWriter", StringComparison.Ordinal);

        private static LogArea AreaFor(string c)
            => c.Contains("ReportIngest", StringComparison.Ordinal) ? LogArea.Reports
             : c.Contains("Telegram", StringComparison.Ordinal) || c.Contains("Alert", StringComparison.Ordinal) ? LogArea.Notify
             : c.Contains("Authentication", StringComparison.Ordinal) ? LogArea.Auth
             : LogArea.System;
    }
}
