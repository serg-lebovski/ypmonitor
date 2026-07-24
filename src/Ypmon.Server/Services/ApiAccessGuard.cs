using System.Net;
using Microsoft.EntityFrameworkCore;
using Ypmon.Server.Data;

namespace Ypmon.Server.Services;

/// <summary>Результат проверки доступа к API по IP.</summary>
public enum ApiAccess
{
    Allow,       // пускаем (режим Off, приватный IP или IP в списке)
    AuditFail,   // не в списке, но режим Audit — пропускаем и логируем
    Block        // не в списке и режим Enforce — блокируем
}

/// <summary>
/// Белый список доступа к API агентов. Разрешённый набор собирается сам:
/// приватные диапазоны (LAN) ∪ внешние IP всех серверов (ExternalIpAddress) ∪ доп. список из настроек.
/// Режим Off/Audit/Enforce берётся из настроек. Набор кэшируется и обновляется раз в 30 секунд,
/// чтобы не бить в БД на каждый запрос агента.
/// </summary>
public class ApiAccessGuard
{
    private readonly IServiceScopeFactory _scopes;
    private readonly object _lock = new();
    private DateTimeOffset _loadedAt = DateTimeOffset.MinValue;

    private string _mode = "Off";
    private List<string> _extra = new();
    private HashSet<string> _serverIps = new(StringComparer.OrdinalIgnoreCase);

    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    public ApiAccessGuard(IServiceScopeFactory scopes) => _scopes = scopes;

    /// <summary>Текущий режим (для показа/логики), с учётом кэша.</summary>
    public string Mode { get { Refresh(); return _mode; } }

    public ApiAccess Check(IPAddress? ip)
    {
        Refresh();
        if (string.Equals(_mode, "Off", StringComparison.OrdinalIgnoreCase)) return ApiAccess.Allow;
        if (ip is null) return Fail();

        // LAN всегда разрешён — локальные агенты и сеть не отрезаются.
        if (IpAllowList.IsPrivate(ip)) return ApiAccess.Allow;

        var norm = ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
        var s = norm.ToString();

        // Внешние IP серверов (точное совпадение) + доп. список (IP и CIDR).
        if (_serverIps.Contains(s)) return ApiAccess.Allow;
        if (IpAllowList.IsAllowed(norm, _extra)) return ApiAccess.Allow;

        return Fail();
    }

    private ApiAccess Fail() =>
        string.Equals(_mode, "Enforce", StringComparison.OrdinalIgnoreCase) ? ApiAccess.Block : ApiAccess.AuditFail;

    /// <summary>Сбросить кэш, чтобы изменения в настройках подхватились сразу.</summary>
    public void Invalidate() { lock (_lock) _loadedAt = DateTimeOffset.MinValue; }

    private void Refresh()
    {
        if (DateTimeOffset.UtcNow - _loadedAt < Ttl) return;
        lock (_lock)
        {
            if (DateTimeOffset.UtcNow - _loadedAt < Ttl) return;
            try
            {
                using var scope = _scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var s = db.Settings.AsNoTracking().FirstOrDefault();
                _mode = string.IsNullOrWhiteSpace(s?.ApiIpFilterMode) ? "Off" : s!.ApiIpFilterMode;
                _extra = IpAllowList.Parse(s?.ApiAllowedIpsExtra);

                // Внешние IP серверов — из хоста поля ExternalIpAddress (терпит host:port, http://…).
                var raw = db.Servers.AsNoTracking()
                    .Where(x => x.ExternalIpAddress != null && x.ExternalIpAddress != "")
                    .Select(x => x.ExternalIpAddress!)
                    .ToList();
                _serverIps = raw
                    .Select(x => AvailabilityMonitor.ExtractHost(x))
                    .Where(h => IPAddress.TryParse(h, out _))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
            }
            catch { /* при сбое БД оставляем прежний снимок; режим Off не заблокирует агентов */ }
            _loadedAt = DateTimeOffset.UtcNow;
        }
    }
}
