using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Ypmon.Shared;

namespace Ypmon.Agent.Services;

/// <summary>
/// Фоновая служба: по расписанию выполняет бэкапы PostgreSQL и архивацию файлов,
/// обслуживает retention и формирует статусы заданий для отчётов.
/// </summary>
public class BackupRunner : BackgroundService
{
    private readonly ConfigStore _store;
    private readonly ILogger<BackupRunner> _log;
    private readonly ConcurrentDictionary<string, byte> _runNow = new();
    private Dictionary<string, JobState> _state;
    private readonly object _stateLock = new();

    public BackupRunner(ConfigStore store, ILogger<BackupRunner> log)
    {
        _store = store;
        _log = log;
        _state = _store.LoadState();
    }

    /// <summary>Запросить немедленное выполнение задания(й). "*" — все.</summary>
    public void RequestRunNow(IEnumerable<string> names)
    {
        foreach (var n in names) _runNow[n] = 1;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickAsync(); }
            catch (Exception ex) { _log.LogError(ex, "Ошибка планировщика бэкапов"); }
            try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); } catch { }
        }
    }

    private async Task TickAsync()
    {
        var cfg = _store.Load();
        // Локальный сигнал «выполнить сейчас»: из этого же процесса (окно) или через флаг-файл
        // (когда задания выполняет служба, а окно открыто отдельным процессом).
        var runAll = _runNow.ContainsKey("*") || _store.ConsumeRunNow();

        foreach (var job in cfg.PostgresJobs)
        {
            if (!job.Enabled) continue;
            var key = $"pg:{job.Name}";
            if (runAll || _runNow.ContainsKey(key) || IsDue(key, job.IntervalMinutes))
            {
                _runNow.TryRemove(key, out _);
                await RunPostgresAsync(cfg, job, key);
            }
        }

        foreach (var job in cfg.FileArchiveJobs)
        {
            if (!job.Enabled) continue;
            var key = $"file:{job.Name}";
            if (runAll || _runNow.ContainsKey(key) || IsDue(key, job.IntervalMinutes))
            {
                _runNow.TryRemove(key, out _);
                RunFileArchive(job, key);
            }
        }

        _runNow.TryRemove("*", out _);
    }

    private bool IsDue(string key, int intervalMinutes)
    {
        if (intervalMinutes <= 0) return false;
        lock (_stateLock)
        {
            if (!_state.TryGetValue(key, out var st) || st.LastRunAt is null) return true;
            return (DateTimeOffset.UtcNow - st.LastRunAt.Value).TotalMinutes >= intervalMinutes;
        }
    }

    private void SetState(string key, Action<JobState> mutate)
    {
        lock (_stateLock)
        {
            if (!_state.TryGetValue(key, out var st)) { st = new JobState(); _state[key] = st; }
            mutate(st);
            _store.SaveState(_state);
        }
    }

    private JobState GetState(string key)
    {
        lock (_stateLock)
            return _state.TryGetValue(key, out var st) ? st : new JobState();
    }

    // --- PostgreSQL ---
    private async Task RunPostgresAsync(AgentConfig cfg, PostgresBackupJob job, string key)
    {
        _log.LogInformation("Старт бэкапа Postgres: {Name}", job.Name);
        try
        {
            if (string.IsNullOrWhiteSpace(job.BackupDir))
                throw new InvalidOperationException("Не указана папка для бэкапов");
            Directory.CreateDirectory(job.BackupDir);

            var ext = job.ExtraArgs.Contains("custom") ? "dump" : "sql";
            var file = Path.Combine(job.BackupDir,
                $"{Sanitize(job.Database)}_{DateTime.Now:yyyyMMdd_HHmmss}.{ext}");

            var psi = new ProcessStartInfo
            {
                FileName = string.IsNullOrWhiteSpace(cfg.PgDumpPath) ? "pg_dump" : cfg.PgDumpPath,
                RedirectStandardError = true,
                RedirectStandardOutput = false,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add($"--host={job.Host}");
            psi.ArgumentList.Add($"--port={job.Port}");
            psi.ArgumentList.Add($"--username={job.Username}");
            psi.ArgumentList.Add($"--file={file}");
            foreach (var a in (job.ExtraArgs ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
                psi.ArgumentList.Add(a);
            psi.ArgumentList.Add(job.Database);
            psi.Environment["PGPASSWORD"] = job.Password;

            using var proc = Process.Start(psi)!;
            var stderr = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();

            if (proc.ExitCode != 0)
            {
                SetState(key, st => { st.LastRunAt = DateTimeOffset.UtcNow; st.LastOutcome = JobOutcome.Error; st.LastMessage = $"pg_dump код {proc.ExitCode}: {Trunc(stderr)}"; });
                _log.LogWarning("Бэкап {Name} завершился с ошибкой: {Err}", job.Name, stderr);
                return;
            }

            ApplyRetention(job.BackupDir, $"{Sanitize(job.Database)}_*", job.RetentionCount);
            SetState(key, st => { st.LastRunAt = DateTimeOffset.UtcNow; st.LastBackupAt = DateTimeOffset.UtcNow; st.LastOutcome = JobOutcome.Ok; st.LastMessage = $"Бэкап создан: {Path.GetFileName(file)}"; });
            _log.LogInformation("Бэкап {Name} успешно создан", job.Name);
        }
        catch (Exception ex)
        {
            SetState(key, st => { st.LastRunAt = DateTimeOffset.UtcNow; st.LastOutcome = JobOutcome.Error; st.LastMessage = ex.Message; });
            _log.LogError(ex, "Ошибка бэкапа {Name}", job.Name);
        }
    }

    // --- Архивация файлов ---
    private void RunFileArchive(FileArchiveJob job, string key)
    {
        _log.LogInformation("Старт архивации: {Name}", job.Name);
        IDisposable? netConn = null;
        try
        {
            if (string.IsNullOrWhiteSpace(job.ArchiveDir))
                throw new InvalidOperationException("Не указана папка для архивов");

            // Подключаем сетевые ресурсы (источники + папка архивов) с учётными данными задания.
            var allPaths = job.SourcePaths.Concat(new[] { job.ArchiveDir });
            netConn = NetShare.Connect(allPaths, job.NetworkUsername, job.NetworkPassword);

            Directory.CreateDirectory(job.ArchiveDir);

            var zipPath = Path.Combine(job.ArchiveDir,
                $"{Sanitize(job.Name)}_{DateTime.Now:yyyyMMdd_HHmmss}.zip");

            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                foreach (var src in job.SourcePaths)
                {
                    if (Directory.Exists(src))
                    {
                        var root = new DirectoryInfo(src).Name;
                        foreach (var f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
                        {
                            var rel = Path.Combine(root, Path.GetRelativePath(src, f));
                            try { zip.CreateEntryFromFile(f, rel); } catch { /* занятый файл пропускаем */ }
                        }
                    }
                    else if (File.Exists(src))
                    {
                        try { zip.CreateEntryFromFile(src, Path.GetFileName(src)); } catch { }
                    }
                }
            }

            ApplyRetention(job.ArchiveDir, $"{Sanitize(job.Name)}_*.zip", job.RetentionCount);
            var size = new FileInfo(zipPath).Length;
            SetState(key, st => { st.LastRunAt = DateTimeOffset.UtcNow; st.LastBackupAt = DateTimeOffset.UtcNow; st.LastOutcome = JobOutcome.Ok; st.LastMessage = $"Архив создан: {Path.GetFileName(zipPath)} ({size / 1024 / 1024} МБ)"; });
        }
        catch (Exception ex)
        {
            SetState(key, st => { st.LastRunAt = DateTimeOffset.UtcNow; st.LastOutcome = JobOutcome.Error; st.LastMessage = ex.Message; });
            _log.LogError(ex, "Ошибка архивации {Name}", job.Name);
        }
        finally
        {
            netConn?.Dispose();
        }
    }

    private void ApplyRetention(string dir, string pattern, int keep)
    {
        if (keep <= 0) return;
        var files = new DirectoryInfo(dir).GetFiles(pattern)
            .OrderByDescending(f => f.CreationTimeUtc).ToList();
        foreach (var old in files.Skip(keep))
        {
            try { old.Delete(); } catch { }
        }
    }

    /// <summary>Сводка статусов всех заданий для отчёта на сервер.</summary>
    public List<JobStatusDto> BuildJobStatuses(AgentConfig cfg)
    {
        var list = new List<JobStatusDto>();

        foreach (var job in cfg.PostgresJobs)
        {
            var st = GetState($"pg:{job.Name}");
            var (count, size) = CountBackups(job.BackupDir, $"{Sanitize(job.Database)}_*");
            list.Add(new JobStatusDto
            {
                Name = job.Name,
                Type = JobType.PostgresBackup,
                Outcome = job.Enabled ? st.LastOutcome : JobOutcome.Unknown,
                Message = job.Enabled ? st.LastMessage : "Отключено",
                LastRunAt = st.LastRunAt,
                LastBackupAt = st.LastBackupAt,
                BackupCount = count,
                TotalSizeBytes = size,
                Target = $"{job.Database} → {job.BackupDir}"
            });
        }

        foreach (var job in cfg.FileArchiveJobs)
        {
            var st = GetState($"file:{job.Name}");
            var (count, size) = CountBackups(job.ArchiveDir, $"{Sanitize(job.Name)}_*.zip");
            list.Add(new JobStatusDto
            {
                Name = job.Name,
                Type = JobType.FileArchive,
                Outcome = job.Enabled ? st.LastOutcome : JobOutcome.Unknown,
                Message = job.Enabled ? st.LastMessage : "Отключено",
                LastRunAt = st.LastRunAt,
                LastBackupAt = st.LastBackupAt,
                BackupCount = count,
                TotalSizeBytes = size,
                Target = job.ArchiveDir
            });
        }

        if (cfg.Mssql.Enabled)
            list.Add(MssqlLogReader.BuildStatus(cfg.Mssql));

        return list;
    }

    private static (int count, long size) CountBackups(string? dir, string pattern)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return (0, 0);
        try
        {
            var files = new DirectoryInfo(dir).GetFiles(pattern);
            return (files.Length, files.Sum(f => f.Length));
        }
        catch { return (0, 0); }
    }

    private static string Sanitize(string s)
        => string.Concat((s ?? "").Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    private static string Trunc(string s) => s.Length > 300 ? s[..300] : s;
}
