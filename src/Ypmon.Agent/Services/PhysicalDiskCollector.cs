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

            // Счётчики надёжности (температура, наработка, износ) — сопоставляем по DeviceId.
            var counters = CollectCounters(scope);
            // Переназначенные сектора берём из «сырых» атрибутов SMART: в счётчиках надёжности их нет.
            var smart = SmartAttributes.Collect();

            var q = new ObjectQuery("SELECT DeviceId, FriendlyName, SerialNumber, HealthStatus, Size FROM MSFT_PhysicalDisk");
            using var searcher = new ManagementObjectSearcher(scope, q);
            foreach (ManagementObject mo in searcher.Get())
            {
                var name = mo["FriendlyName"]?.ToString()?.Trim();
                var serial = mo["SerialNumber"]?.ToString()?.Trim();
                var deviceId = mo["DeviceId"]?.ToString();
                long size = 0; try { size = Convert.ToInt64(mo["Size"]); } catch { }

                DiskCounters? c = null;
                if (deviceId is not null) counters.TryGetValue(deviceId, out c);

                SmartValues? sm = null;
                if (deviceId is not null && int.TryParse(deviceId, out var idx)) smart.TryGetValue(idx, out sm);

                list.Add(new PhysicalDiskDto
                {
                    Name = string.IsNullOrWhiteSpace(name) ? "Накопитель" : name,
                    Serial = string.IsNullOrWhiteSpace(serial) ? null : serial,
                    Health = HealthText(mo["HealthStatus"]),
                    TemperatureC = c?.TemperatureC,
                    SizeBytes = size,
                    // Наработка: счётчик надёжности точнее, но есть не везде — тогда берём атрибут SMART 09.
                    PowerOnHours = c?.PowerOnHours ?? sm?.PowerOnHours,
                    WearPercent = c?.WearPercent,
                    ReallocatedSectors = sm?.ReallocatedSectors
                });
            }
        }
        catch { /* WMI недоступен / нет прав — вернём то, что успели собрать */ }
        return list;
    }

    /// <summary>Показания счётчика надёжности накопителя.</summary>
    private sealed record DiskCounters(int? TemperatureC, int? PowerOnHours, int? WearPercent);

    private static Dictionary<string, DiskCounters> CollectCounters(ManagementScope scope)
    {
        var result = new Dictionary<string, DiskCounters>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var q = new ObjectQuery("SELECT DeviceId, Temperature, PowerOnHours, Wear FROM MSFT_StorageReliabilityCounter");
            using var searcher = new ManagementObjectSearcher(scope, q);
            foreach (ManagementObject mo in searcher.Get())
            {
                var id = mo["DeviceId"]?.ToString();
                if (id is null) continue;

                int? temp = null, poh = null, wear = null;
                try { var t = Convert.ToInt32(mo["Temperature"]); if (t > 0 && t < 200) temp = t; } catch { }
                // Наработка бывает заведомо мусорной (0 или абсурдные значения) — 60 лет заведомо больше правды.
                try { var h = Convert.ToInt32(mo["PowerOnHours"]); if (h > 0 && h < 525_600) poh = h; } catch { }
                try { var w = Convert.ToInt32(mo["Wear"]); if (w >= 0 && w <= 100) wear = w; } catch { }

                result[id] = new DiskCounters(temp, poh, wear);
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
