using System.Management;
using System.Runtime.Versioning;

namespace Ypmon.Agent.Services;

/// <summary>Значения SMART, которые умеем разбирать.</summary>
public sealed record SmartValues(int? PowerOnHours, int? ReallocatedSectors);

/// <summary>
/// Чтение «сырых» атрибутов SMART через <c>MSStorageDriver_FailurePredictData</c> (root\WMI).
/// Нужно то, чего нет в счётчиках надёжности Windows: переназначенные сектора.
/// Требует прав администратора — служба агента под ними и работает.
/// Только чтение, best-effort: у виртуальных дисков, NVMe и части RAID-контроллеров данных нет.
/// </summary>
[SupportedOSPlatform("windows")]
public static class SmartAttributes
{
    private const byte AttrReallocatedSectors = 0x05;
    private const byte AttrPowerOnHours = 0x09;

    /// <summary>Атрибуты по номеру физического диска (совпадает с DeviceId в MSFT_PhysicalDisk).</summary>
    public static Dictionary<int, SmartValues> Collect()
    {
        var result = new Dictionary<int, SmartValues>();
        try
        {
            // Сопоставление «номер диска → PNPDeviceID»: имена экземпляров SMART начинаются именно с него.
            var byPnp = new List<(int Index, string Pnp)>();
            using (var drives = new ManagementObjectSearcher("SELECT Index, PNPDeviceID FROM Win32_DiskDrive"))
            {
                foreach (ManagementObject mo in drives.Get())
                {
                    var pnp = mo["PNPDeviceID"]?.ToString();
                    if (string.IsNullOrWhiteSpace(pnp)) continue;
                    try { byPnp.Add((Convert.ToInt32(mo["Index"]), pnp!)); } catch { }
                }
            }
            if (byPnp.Count == 0) return result;

            var scope = new ManagementScope(@"\\.\root\WMI");
            scope.Connect();
            using var searcher = new ManagementObjectSearcher(scope,
                new ObjectQuery("SELECT InstanceName, VendorSpecific FROM MSStorageDriver_FailurePredictData"));

            foreach (ManagementObject mo in searcher.Get())
            {
                var instance = mo["InstanceName"]?.ToString();
                if (instance is null || mo["VendorSpecific"] is not byte[] data) continue;

                var match = byPnp.FirstOrDefault(x => instance.StartsWith(x.Pnp, StringComparison.OrdinalIgnoreCase));
                if (match.Pnp is null) continue;

                var parsed = Parse(data);
                if (parsed is not null) result[match.Index] = parsed;
            }
        }
        catch { }
        return result;
    }

    /// <summary>
    /// Разбор блока атрибутов SMART: первые 2 байта — версия, дальше по 12 байт на атрибут
    /// (номер, флаги, текущее значение, худшее, 6 байт «сырого» значения, резерв).
    /// </summary>
    private static SmartValues? Parse(byte[] data)
    {
        if (data.Length < 2 + 12) return null;

        int? poh = null, realloc = null;
        for (var offset = 2; offset + 12 <= data.Length; offset += 12)
        {
            var id = data[offset];
            if (id == 0) continue;   // пустой слот

            // «Сырое» значение — 6 байт little-endian; берём младшие 4, старшие у этих атрибутов нулевые.
            long raw = data[offset + 5]
                     | ((long)data[offset + 6] << 8)
                     | ((long)data[offset + 7] << 16)
                     | ((long)data[offset + 8] << 24);

            if (id == AttrPowerOnHours && raw > 0 && raw < 525_600) poh = (int)raw;
            else if (id == AttrReallocatedSectors && raw >= 0 && raw < 1_000_000) realloc = (int)raw;
        }

        return poh is null && realloc is null ? null : new SmartValues(poh, realloc);
    }
}
