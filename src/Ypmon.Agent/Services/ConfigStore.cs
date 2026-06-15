using System.Text.Json;
using Ypmon.Shared;

namespace Ypmon.Agent.Services;

/// <summary>
/// Загрузка/сохранение конфигурации агента, состояния заданий и снапшота статуса (потокобезопасно).
/// Всё хранится в файлах рядом с исполняемым файлом — обмена с сервером для настройки нет.
/// </summary>
public class ConfigStore
{
    private readonly string _configPath;
    private readonly string _statePath;
    private readonly string _snapshotPath;
    private readonly string _runNowFlagPath;
    private readonly object _lock = new();
    private static readonly JsonSerializerOptions JsonOpts =
        new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public ConfigStore()
    {
        var dir = AppContext.BaseDirectory;
        _configPath = Path.Combine(dir, "config.json");
        _statePath = Path.Combine(dir, "state.json");
        _snapshotPath = Path.Combine(dir, "snapshot.json");
        _runNowFlagPath = Path.Combine(dir, "runnow.flag");
    }

    public string ConfigPath => _configPath;

    // --- Конфигурация ---
    public AgentConfig Load()
    {
        lock (_lock)
        {
            if (!File.Exists(_configPath))
            {
                var def = new AgentConfig();
                File.WriteAllText(_configPath, JsonSerializer.Serialize(def, JsonOpts));
                return def;
            }
            try { return JsonSerializer.Deserialize<AgentConfig>(File.ReadAllText(_configPath), JsonOpts) ?? new AgentConfig(); }
            catch { return new AgentConfig(); }
        }
    }

    public void Save(AgentConfig cfg)
    {
        lock (_lock)
            File.WriteAllText(_configPath, JsonSerializer.Serialize(cfg, JsonOpts));
    }

    // --- Состояние заданий (расписание) ---
    public Dictionary<string, JobState> LoadState()
    {
        lock (_lock)
        {
            if (!File.Exists(_statePath)) return new();
            try { return JsonSerializer.Deserialize<Dictionary<string, JobState>>(File.ReadAllText(_statePath), JsonOpts) ?? new(); }
            catch { return new(); }
        }
    }

    public void SaveState(Dictionary<string, JobState> state)
    {
        lock (_lock)
            File.WriteAllText(_statePath, JsonSerializer.Serialize(state, JsonOpts));
    }

    // --- Снапшот статуса (для окна настроек, читается из файла) ---
    public AgentSnapshot LoadSnapshot()
    {
        lock (_lock)
        {
            if (!File.Exists(_snapshotPath)) return new();
            try { return JsonSerializer.Deserialize<AgentSnapshot>(File.ReadAllText(_snapshotPath), JsonOpts) ?? new(); }
            catch { return new(); }
        }
    }

    public void SaveSnapshot(AgentSnapshot s)
    {
        lock (_lock)
        {
            try { File.WriteAllText(_snapshotPath, JsonSerializer.Serialize(s, JsonOpts)); } catch { }
        }
    }

    // --- Локальный сигнал «выполнить сейчас» (флаг-файл; задаётся только из окна агента) ---
    public void SignalRunNow()
    {
        lock (_lock)
        {
            try { File.WriteAllText(_runNowFlagPath, DateTimeOffset.UtcNow.ToString("o")); } catch { }
        }
    }

    public bool ConsumeRunNow()
    {
        lock (_lock)
        {
            if (!File.Exists(_runNowFlagPath)) return false;
            try { File.Delete(_runNowFlagPath); } catch { }
            return true;
        }
    }
}

/// <summary>Состояние выполнения одного задания (для расписания и отчётов).</summary>
public class JobState
{
    public DateTimeOffset? LastRunAt { get; set; }
    public DateTimeOffset? LastBackupAt { get; set; }
    public JobOutcome LastOutcome { get; set; } = JobOutcome.Unknown;
    public string? LastMessage { get; set; }
}
