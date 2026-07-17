using System.Management;
using System.Runtime.Versioning;
using Ypmon.Shared;

namespace Ypmon.Agent.Services;

/// <summary>
/// Сбор здоровья физических накопителей (SMART) через WMI-провайдер хранилища Windows
/// (<c>MSFT_PhysicalDisk</c>). Только чтение, best-effort: на виртуальных дисках или без прав
/// данные могут отсутствовать — тогда возвращается пустой список.
/// </summary>
[SupportedOSPlatform("windows")]
public static class PhysicalDiskCollector
{
    public static List<PhysicalDiskDto> Collect()
    {
        var list = new List<PhysicalDiskDto>();
        try
        {
            var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
            scope.Connect();

            // Температуры — из счётчиков надёжности (по возможности), сопоставляем по DeviceId.
            var temps = CollectTemperatures(scope);

            var q = new ObjectQuery("SELECT DeviceId, FriendlyName, SerialNumber, HealthStatus, Size FROM MSFT_PhysicalDisk");
            using var searcher = new ManagementObjectSearcher(scope, q);
            foreach (ManagementObject mo in searcher.Get())
            {
                var name = mo["FriendlyName"]?.ToString()?.Trim();
                var serial = mo["SerialNumber"]?.ToString()?.Trim();
                var deviceId = mo["DeviceId"]?.ToString();
                long size = 0; try { size = Convert.ToInt64(mo["Size"]); } catch { }

                int? temp = null;
                if (deviceId is not null && temps.TryGetValue(deviceId, out var tv)) temp = tv;

                list.Add(new PhysicalDiskDto
                {
                    Name = string.IsNullOrWhiteSpace(name) ? "Накопитель" : name,
                    Serial = string.IsNullOrWhiteSpace(serial) ? null : serial,
                    Health = HealthText(mo["HealthStatus"]),
                    TemperatureC = temp,
                    SizeBytes = size
                });
            }
        }
        catch { /* WMI недоступен / нет прав — вернём то, что успели собрать */ }
        return list;
    }

    private static Dictionary<string, int> CollectTemperatures(ManagementScope scope)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var q = new ObjectQuery("SELECT DeviceId, Temperature FROM MSFT_StorageReliabilityCounter");
            using var searcher = new ManagementObjectSearcher(scope, q);
            foreach (ManagementObject mo in searcher.Get())
            {
                var id = mo["DeviceId"]?.ToString();
                if (id is null) continue;
                try
                {
                    var t = Convert.ToInt32(mo["Temperature"]);
                    if (t > 0 && t < 200) result[id] = t;   // отсеиваем явный мусор
                }
                catch { }
            }
        }
        catch { }
        return result;
    }

    private static string HealthText(object? v)
    {
        if (v is null) return "Unknown";
        try
        {
            return Convert.ToInt32(v) switch
            {
                0 => "Healthy",
                1 => "Warning",
                2 => "Unhealthy",
                _ => "Unknown"
            };
        }
        catch { return "Unknown"; }
    }
}
