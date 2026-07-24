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

    /// <summary>Локальный/приватный адрес (LAN): RFC1918, loopback, link-local, ULA IPv6.</summary>
    public static bool IsPrivate(IPAddress? ip)
    {
        if (ip is null) return false;
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();

        var b = ip.GetAddressBytes();
        if (ip.AddressFamily == AddressFamily.InterNetwork && b.Length == 4)
        {
            if (b[0] == 10) return true;                          // 10.0.0.0/8
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true; // 172.16.0.0/12
            if (b[0] == 192 && b[1] == 168) return true;          // 192.168.0.0/16
            if (b[0] == 169 && b[1] == 254) return true;          // 169.254.0.0/16 link-local
            return false;
        }
        if (ip.AddressFamily == AddressFamily.InterNetworkV6 && b.Length == 16)
        {
            if ((b[0] & 0xFE) == 0xFC) return true;               // fc00::/7 unique local
            if (b[0] == 0xFE && (b[1] & 0xC0) == 0x80) return true; // fe80::/10 link-local
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
