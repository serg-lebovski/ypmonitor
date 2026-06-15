using Ypmon.Shared;

namespace Ypmon.Agent.Services;

public static class SystemInfo
{
    public static List<DiskInfoDto> GetDisks()
    {
        var list = new List<DiskInfoDto>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady || d.DriveType != DriveType.Fixed) continue;
                list.Add(new DiskInfoDto
                {
                    Name = d.Name,
                    TotalBytes = d.TotalSize,
                    FreeBytes = d.AvailableFreeSpace
                });
            }
            catch { /* недоступный диск пропускаем */ }
        }
        return list;
    }

    public static long UptimeSeconds() => Environment.TickCount64 / 1000;
}
