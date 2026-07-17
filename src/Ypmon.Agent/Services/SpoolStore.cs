using System.Text.Json;
using Ypmon.Shared;

namespace Ypmon.Agent.Services;

/// <summary>
/// Локальная очередь полных отчётов (store-and-forward): при недоступности сервера отчёт
/// складывается на диск (рядом с exe, папка <c>spool</c>) и досылается при восстановлении связи.
/// Отчёты старше 3 суток удаляются автоматически; есть жёсткий предел числа файлов.
/// Heartbeat'ы не копятся — важен только последний.
/// </summary>
public class SpoolStore
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(3);
    private const int MaxFiles = 500;               // страховка от переполнения диска
    private readonly string _dir = Path.Combine(AppContext.BaseDirectory, "spool");
    private readonly object _lock = new();
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Положить отчёт в очередь (сервер был недоступен).</summary>
    public void Enqueue(AgentReportDto report)
    {
        lock (_lock)
        {
            try
            {
                Directory.CreateDirectory(_dir);
                Prune();
                // Имя = тики UTC (D19) — файлы естественно сортируются по времени.
                var name = DateTimeOffset.UtcNow.Ticks.ToString("D19") + ".json";
                File.WriteAllText(Path.Combine(_dir, name), JsonSerializer.Serialize(report));
            }
            catch { /* сбой записи очереди не должен ронять отправку */ }
        }
    }

    /// <summary>Сколько отчётов сейчас в очереди.</summary>
    public int Count()
    {
        try { return Directory.Exists(_dir) ? Directory.GetFiles(_dir, "*.json").Length : 0; }
        catch { return 0; }
    }

    /// <summary>
    /// Досылает накопленные отчёты по порядку (старые первыми). Останавливается на первой
    /// неудаче (сервер снова недоступен). Возвращает число успешно отправленных.
    /// </summary>
    public async Task<int> FlushAsync(Func<AgentReportDto, Task<bool>> sendAsync)
    {
        List<string> files;
        lock (_lock)
        {
            if (!Directory.Exists(_dir)) return 0;
            Prune();
            files = Directory.GetFiles(_dir, "*.json").OrderBy(f => f, StringComparer.Ordinal).ToList();
        }

        int sent = 0;
        foreach (var f in files)
        {
            AgentReportDto? rep;
            try { rep = JsonSerializer.Deserialize<AgentReportDto>(File.ReadAllText(f), JsonOpts); }
            catch { TryDelete(f); continue; }        // битый файл — выбрасываем
            if (rep is null) { TryDelete(f); continue; }

            bool ok;
            try { ok = await sendAsync(rep); }
            catch { ok = false; }
            if (!ok) break;                          // сервер снова недоступен — остальное подождёт
            TryDelete(f);
            sent++;
        }
        return sent;
    }

    private void Prune()
    {
        try
        {
            if (!Directory.Exists(_dir)) return;
            var cutoff = DateTimeOffset.UtcNow - MaxAge;
            foreach (var f in Directory.GetFiles(_dir, "*.json"))
            {
                DateTimeOffset ts;
                if (long.TryParse(Path.GetFileNameWithoutExtension(f), out var ticks))
                    ts = new DateTimeOffset(ticks, TimeSpan.Zero);
                else { try { ts = File.GetLastWriteTimeUtc(f); } catch { continue; } }
                if (ts < cutoff) TryDelete(f);
            }
            // Жёсткий предел количества — удаляем самые старые сверх лимита.
            var remaining = Directory.GetFiles(_dir, "*.json").OrderBy(f => f, StringComparer.Ordinal).ToList();
            for (int i = 0; i < remaining.Count - MaxFiles; i++) TryDelete(remaining[i]);
        }
        catch { }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }
}
