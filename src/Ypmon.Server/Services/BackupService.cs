using System.Diagnostics;
using System.IO.Compression;
using Npgsql;

namespace Ypmon.Server.Services;

/// <summary>
/// Резервное копирование базы данных: делает дамп и отдаёт его файлом на скачивание.
/// Postgres — через pg_dump (обычный SQL, сжатый gzip), sqlite — копия файла БД через VACUUM INTO.
/// </summary>
public class BackupService
{
    private readonly IConfiguration _cfg;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<BackupService> _log;

    public BackupService(IConfiguration cfg, IWebHostEnvironment env, ILogger<BackupService> log)
    {
        _cfg = cfg; _env = env; _log = log;
    }

    public string Provider => (_cfg["Database:Provider"] ?? "sqlite").ToLowerInvariant();

    /// <summary>Есть ли на сервере всё необходимое для дампа (для postgres — утилита pg_dump).</summary>
    public bool IsAvailable => Provider != "postgres" || PgDumpPath is not null;

    /// <summary>Путь к pg_dump или null, если утилита не установлена в контейнере.</summary>
    public static string? PgDumpPath
    {
        get
        {
            foreach (var p in new[] { "/usr/bin/pg_dump", "/usr/local/bin/pg_dump", "/usr/lib/postgresql/16/bin/pg_dump" })
                if (File.Exists(p)) return p;
            return null;
        }
    }

    /// <summary>
    /// Создаёт дамп во временный файл и возвращает путь и имя для скачивания.
    /// Вызывающий обязан удалить временный файл после отдачи.
    /// </summary>
    public async Task<(string tempPath, string fileName)> CreateAsync(CancellationToken ct = default)
    {
        var stamp = TelegramReportService.OmskNow().ToString("yyyyMMdd-HHmmss");
        var tmpDir = Path.Combine(_env.ContentRootPath, "data", "backup-tmp");
        Directory.CreateDirectory(tmpDir);

        return Provider == "postgres"
            ? (await DumpPostgresAsync(tmpDir, ct), $"ypmon-{stamp}.sql.gz")
            : (DumpSqlite(tmpDir), $"ypmon-{stamp}.db");
    }

    private async Task<string> DumpPostgresAsync(string tmpDir, CancellationToken ct)
    {
        var exe = PgDumpPath ?? throw new InvalidOperationException(
            "На сервере нет утилиты pg_dump. Обновите сервер (Настройки → Обновление сервера), она добавляется при пересборке образа.");

        var csb = new NpgsqlConnectionStringBuilder(_cfg["Database:ConnectionString"]);
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("--host=" + (csb.Host ?? "postgres"));
        psi.ArgumentList.Add("--port=" + (csb.Port > 0 ? csb.Port : 5432));
        psi.ArgumentList.Add("--username=" + csb.Username);
        psi.ArgumentList.Add("--dbname=" + csb.Database);
        psi.ArgumentList.Add("--no-owner");
        psi.ArgumentList.Add("--no-privileges");
        psi.ArgumentList.Add("--clean");
        psi.ArgumentList.Add("--if-exists");
        psi.Environment["PGPASSWORD"] = csb.Password ?? "";

        var path = Path.Combine(tmpDir, Guid.NewGuid().ToString("N") + ".sql.gz");
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Не удалось запустить pg_dump.");

        var errTask = proc.StandardError.ReadToEndAsync();
        await using (var file = File.Create(path))
        await using (var gz = new GZipStream(file, CompressionLevel.Optimal))
            await proc.StandardOutput.BaseStream.CopyToAsync(gz, ct);

        await proc.WaitForExitAsync(ct);
        var err = await errTask;
        if (proc.ExitCode != 0)
        {
            TryDelete(path);
            _log.LogError("pg_dump завершился с кодом {Code}: {Err}", proc.ExitCode, err);
            throw new InvalidOperationException($"pg_dump вернул ошибку: {err.Trim()}");
        }
        return path;
    }

    /// <summary>Согласованная копия sqlite-базы (VACUUM INTO работает без остановки сервера).</summary>
    private string DumpSqlite(string tmpDir)
    {
        var conn = _cfg["Database:ConnectionString"];
        if (string.IsNullOrWhiteSpace(conn))
            conn = $"Data Source={Path.Combine(_env.ContentRootPath, "data", "ypmon.db")}";

        var path = Path.Combine(tmpDir, Guid.NewGuid().ToString("N") + ".db");
        using var c = new Microsoft.Data.Sqlite.SqliteConnection(conn);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "VACUUM INTO $p";
        cmd.Parameters.AddWithValue("$p", path);
        cmd.ExecuteNonQuery();
        return path;
    }

    public static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    /// <summary>Удаляет забытые временные дампы (например, если скачивание оборвалось).</summary>
    public void PruneTemp()
    {
        var tmpDir = Path.Combine(_env.ContentRootPath, "data", "backup-tmp");
        if (!Directory.Exists(tmpDir)) return;
        foreach (var f in Directory.GetFiles(tmpDir))
            if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddHours(-6)) TryDelete(f);
    }
}
