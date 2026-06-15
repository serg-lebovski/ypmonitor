using System.Diagnostics;

namespace Ypmon.Agent.Services;

/// <summary>Поиск исполняемого файла pg_dump: по заданному пути, в PATH или в стандартных папках PostgreSQL.</summary>
public static class PgDumpResolver
{
    /// <summary>Возвращает рабочий путь к pg_dump или null, если не найден.</summary>
    public static string? Resolve(string? configured)
    {
        // 1) Явно заданный существующий файл
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (File.Exists(configured)) return configured;
            // если указали папку bin — добавим имя файла
            try
            {
                if (Directory.Exists(configured))
                {
                    var inDir = Path.Combine(configured, "pg_dump.exe");
                    if (File.Exists(inDir)) return inDir;
                }
            }
            catch { }
        }

        // 2) В PATH
        var inPath = FindInPath("pg_dump.exe");
        if (inPath is not null) return inPath;

        // 3) Стандартные папки установки PostgreSQL
        foreach (var pf in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
                 })
        {
            var baseDir = Path.Combine(pf, "PostgreSQL");
            if (!Directory.Exists(baseDir)) continue;
            try
            {
                // самые свежие версии сначала
                foreach (var ver in Directory.GetDirectories(baseDir).OrderByDescending(d => d))
                {
                    var exe = Path.Combine(ver, "bin", "pg_dump.exe");
                    if (File.Exists(exe)) return exe;
                }
            }
            catch { }
        }

        return null;
    }

    private static string? FindInPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var full = Path.Combine(dir.Trim(), fileName);
                if (File.Exists(full)) return full;
            }
            catch { }
        }
        return null;
    }

    /// <summary>Версия pg_dump (для отображения), либо сообщение об ошибке.</summary>
    public static (bool ok, string message, string? path) Check(string? configured)
    {
        var resolved = Resolve(configured);
        if (resolved is null)
            return (false, "pg_dump не найден. Установите PostgreSQL или укажите полный путь к pg_dump.exe.", null);
        try
        {
            var psi = new ProcessStartInfo(resolved, "--version")
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi)!;
            var outp = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(5000);
            return (true, $"Найден: {resolved}\n{outp}", resolved);
        }
        catch (Exception ex)
        {
            return (false, $"Найден {resolved}, но не запускается: {ex.Message}", resolved);
        }
    }
}
