using Ypmon.Shared;

namespace Ypmon.Agent.Services;

/// <summary>
/// Проверяет наблюдаемые папки: количество файлов (любого формата), объём и дата самого свежего.
/// Никаких изменений в системе — только чтение. Порог устаревания вычисляет сервер.
/// </summary>
public class FolderMonitorService
{
    public List<FolderStatusDto> Build(AgentConfig cfg)
    {
        var list = new List<FolderStatusDto>();
        foreach (var f in cfg.Folders)
            if (f.Enabled)
                list.Add(Check(f));
        return list;
    }

    public FolderStatusDto Check(MonitoredFolder folder)
    {
        var st = new FolderStatusDto { Name = folder.Name, Path = folder.Path };
        IDisposable? net = null;
        try
        {
            net = NetShare.Connect(new[] { folder.Path }, folder.NetworkUsername, folder.NetworkPassword);

            if (string.IsNullOrWhiteSpace(folder.Path) || !Directory.Exists(folder.Path))
            {
                st.Accessible = false;
                st.Message = "Папка не найдена или недоступна: " + folder.Path;
                return st;
            }

            var files = new DirectoryInfo(folder.Path).GetFiles("*", SearchOption.TopDirectoryOnly);
            st.Accessible = true;
            st.FileCount = files.Length;
            st.TotalSizeBytes = files.Sum(x => x.Length);
            var latest = files.OrderByDescending(x => x.LastWriteTimeUtc).FirstOrDefault();
            if (latest is not null)
            {
                st.LastBackupAt = new DateTimeOffset(latest.LastWriteTimeUtc, TimeSpan.Zero);
                st.LastFileName = latest.Name;
            }
            st.Message = files.Length == 0
                ? "В папке нет файлов"
                : $"Файлов: {files.Length}, свежий: {latest!.Name} ({latest.LastWriteTime:yyyy-MM-dd HH:mm})";
        }
        catch (Exception ex)
        {
            st.Accessible = false;
            st.Message = "Ошибка чтения папки: " + ex.Message;
        }
        finally { net?.Dispose(); }
        return st;
    }
}
