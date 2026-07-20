using System.Management;
using System.Runtime.Versioning;

namespace Ypmon.Agent.Services;

/// <summary>
/// Температура процессора через WMI. Best-effort: единого способа на Windows нет,
/// поэтому пробуем источники по очереди и возвращаем первый сработавший.
///
/// ВАЖНО: на многих машинах датчик недоступен в принципе — ACPI-термозона часто не реализована
/// (особенно на серверном железе и в виртуальных машинах), а прямое чтение с датчиков материнской
/// платы требует драйвера. Тогда возвращается null, и это нормальная ситуация, а не ошибка.
/// </summary>
[SupportedOSPlatform("windows")]
public static class CpuTemperatureCollector
{
    /// <summary>Причина, по которой в прошлый раз не удалось получить температуру (для журнала и интерфейса).</summary>
    public static string? LastUnavailableReason { get; private set; }

    public static double? Read()
    {
        // 1) ACPI-термозона: есть на большинстве ноутбуков и части десктопов.
        var (t, denied) = FromThermalZone();
        if (t is not null) { LastUnavailableReason = null; return t; }

        // 2) LibreHardwareMonitor / OpenHardwareMonitor — если установлены, дают точные датчики CPU.
        t = FromHardwareMonitor(@"\\.\root\LibreHardwareMonitor") ?? FromHardwareMonitor(@"\\.\root\OpenHardwareMonitor");
        if (t is not null) { LastUnavailableReason = null; return t; }

        // Отказ в доступе — важный отдельный случай: датчик есть, но нужны права администратора.
        // Служба агента работает под LocalSystem и читает его нормально, а вот окно настроек,
        // запущенное обычным пользователем, — нет.
        LastUnavailableReason = denied
            ? "нет прав на чтение датчика (нужен запуск от администратора; служба агента читает его нормально)"
            : "нет ACPI-термозоны (частое на серверном железе и в виртуальных машинах) и не установлен LibreHardwareMonitor";
        return null;
    }

    /// <summary>
    /// MSAcpi_ThermalZoneTemperature отдаёт десятые доли Кельвина.
    /// Второй элемент — признак «отказано в доступе» (нужны права администратора).
    /// </summary>
    private static (double? value, bool accessDenied) FromThermalZone()
    {
        try
        {
            var scope = new ManagementScope(@"\\.\root\WMI");
            scope.Connect();
            using var searcher = new ManagementObjectSearcher(scope,
                new ObjectQuery("SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature"));

            double max = double.MinValue;
            foreach (ManagementObject mo in searcher.Get())
            {
                try
                {
                    var raw = Convert.ToDouble(mo["CurrentTemperature"]);
                    var c = raw / 10.0 - 273.15;
                    // Отсеиваем мусор: реальная температура процессора не бывает вне этого диапазона.
                    if (c > 0 && c < 150 && c > max) max = c;
                }
                catch { }
            }
            return (max > double.MinValue ? Math.Round(max, 1) : null, false);
        }
        catch (ManagementException ex) when (ex.ErrorCode == ManagementStatus.AccessDenied)
        {
            return (null, true);
        }
        catch (UnauthorizedAccessException) { return (null, true); }
        catch { return (null, false); }
    }

    /// <summary>Датчики Libre/OpenHardwareMonitor: берём максимум по температурным датчикам CPU.</summary>
    private static double? FromHardwareMonitor(string scopePath)
    {
        try
        {
            var scope = new ManagementScope(scopePath);
            scope.Connect();
            using var searcher = new ManagementObjectSearcher(scope,
                new ObjectQuery("SELECT Identifier, Value FROM Sensor WHERE SensorType = 'Temperature'"));

            double max = double.MinValue;
            foreach (ManagementObject mo in searcher.Get())
            {
                var id = mo["Identifier"]?.ToString() ?? "";
                if (!id.Contains("/cpu", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    var c = Convert.ToDouble(mo["Value"]);
                    if (c > 0 && c < 150 && c > max) max = c;
                }
                catch { }
            }
            return max > double.MinValue ? Math.Round(max, 1) : null;
        }
        catch { return null; }
    }
}
