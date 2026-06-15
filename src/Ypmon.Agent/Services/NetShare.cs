using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Ypmon.Agent.Services;

/// <summary>
/// Подключение к сетевым папкам (UNC) с указанными учётными данными Windows через WNetAddConnection2.
/// Используется для доступа к источникам и папке архивов, лежащим на сетевых ресурсах.
/// </summary>
public static class NetShare
{
    [StructLayout(LayoutKind.Sequential)]
    private struct NETRESOURCE
    {
        public int dwScope, dwType, dwDisplayType, dwUsage;
        public string? lpLocalName, lpRemoteName, lpComment, lpProvider;
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetAddConnection2(ref NETRESOURCE netResource, string? password, string? username, int flags);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetCancelConnection2(string name, int flags, bool force);

    private const int RESOURCETYPE_DISK = 0x1;

    public static bool IsUnc(string? path) => !string.IsNullOrWhiteSpace(path) && path.StartsWith(@"\\");

    /// <summary>Корень сетевого ресурса \\сервер\шара из произвольного пути под ним.</summary>
    public static string? ShareRoot(string path)
    {
        if (!IsUnc(path)) return null;
        var parts = path.TrimEnd('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        // parts[0]=сервер, parts[1]=шара
        return parts.Length >= 2 ? $@"\\{parts[0]}\{parts[1]}" : null;
    }

    /// <summary>
    /// Подключает все сетевые ресурсы, встречающиеся в путях, с заданными учётными данными.
    /// Возвращает объект, при Dispose отключающий соединения. Если креды пустые — ничего не делает.
    /// </summary>
    public static IDisposable Connect(IEnumerable<string> paths, string username, string password)
    {
        var scope = new ConnectionScope();
        if (string.IsNullOrWhiteSpace(username)) return scope;

        var roots = paths.Where(IsUnc)
            .Select(ShareRoot)
            .Where(r => r is not null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var root in roots)
        {
            var nr = new NETRESOURCE { dwType = RESOURCETYPE_DISK, lpRemoteName = root };
            // Сначала пытаемся отключить возможное старое соединение к этому ресурсу.
            WNetCancelConnection2(root!, 0, true);
            var rc = WNetAddConnection2(ref nr, password, username, 0);
            if (rc == 0) scope.Add(root!);
            else throw new Win32Exception(rc, $"Не удалось подключить {root}: код {rc}");
        }
        return scope;
    }

    /// <summary>Проверка доступа к пути (с учётными данными, если путь сетевой).</summary>
    public static (bool ok, string message) CheckAccess(string path, string username, string password)
    {
        if (string.IsNullOrWhiteSpace(path)) return (false, "Путь не указан");
        try
        {
            using var _ = Connect(new[] { path }, username, password);
            var exists = Directory.Exists(path) || File.Exists(path);
            return exists
                ? (true, "Доступ есть")
                : (false, "Подключение прошло, но путь не найден");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private sealed class ConnectionScope : IDisposable
    {
        private readonly List<string> _connected = new();
        public void Add(string root) => _connected.Add(root);
        public void Dispose()
        {
            foreach (var r in _connected)
                try { WNetCancelConnection2(r, 0, true); } catch { }
            _connected.Clear();
        }
    }
}
