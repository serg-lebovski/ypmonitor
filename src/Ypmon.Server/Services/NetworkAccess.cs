using System.Net;
using System.Net.Sockets;

namespace Ypmon.Server.Services;

/// <summary>Порты сервера (доступны из Razor-страниц для показа адреса агенту).</summary>
public static class ServerPorts
{
    public static int WebPort = 8080;
    public static int ReportsPort = 8081;
    public static bool SeparatePorts => ReportsPort != WebPort;
}

/// <summary>Проверка IP по списку разрешённых (одиночные адреса и CIDR-диапазоны).</summary>
public static class IpAllowList
{
    public static List<string> Parse(string? csv) =>
        (csv ?? "").Split(new[] { ',', ';', ' ', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                   .Select(x => x.Trim()).Where(x => x.Length > 0).ToList();

    public static bool IsAllowed(IPAddress? ip, List<string> allow)
    {
        if (allow.Count == 0) return true;            // список пуст = разрешено всем
        if (ip is null) return false;
        if (IPAddress.IsLoopback(ip)) return true;    // localhost всегда разрешён (healthcheck)

        // нормализуем IPv4-mapped IPv6 (::ffff:x.x.x.x)
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();

        foreach (var entry in allow)
        {
            if (entry.Contains('/'))
            {
                if (InCidr(ip, entry)) return true;
            }
            else if (IPAddress.TryParse(entry, out var single) && single.Equals(ip))
            {
                return true;
            }
        }
        return false;
    }

    private static bool InCidr(IPAddress ip, string cidr)
    {
        var parts = cidr.Split('/');
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var net) || !int.TryParse(parts[1], out var bits))
            return false;
        if (ip.AddressFamily != net.AddressFamily) return false;

        var ipb = ip.GetAddressBytes();
        var netb = net.GetAddressBytes();
        if (ipb.Length != netb.Length) return false;

        int fullBytes = bits / 8, remBits = bits % 8;
        for (int i = 0; i < fullBytes; i++)
            if (ipb[i] != netb[i]) return false;
        if (remBits > 0)
        {
            int mask = (byte)(0xFF << (8 - remBits));
            if ((ipb[fullBytes] & mask) != (netb[fullBytes] & mask)) return false;
        }
        return true;
    }
}
