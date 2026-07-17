using System.Security.Claims;
using Ypmon.Server.Data;

namespace Ypmon.Server.Services;

/// <summary>Журнал действий администратора: кто, когда и что сделал.</summary>
public class AuditService
{
    private readonly AppDbContext _db;
    public AuditService(AppDbContext db) => _db = db;

    /// <summary>
    /// Записать действие. Вызывать ПОСЛЕ успешного сохранения основного изменения —
    /// метод сам делает SaveChanges (общий scoped-контекст), чтобы не флашить чужие правки раньше времени.
    /// </summary>
    public async Task LogAsync(ClaimsPrincipal user, string action, string? details = null)
    {
        try
        {
            _db.Audit.Add(new AuditEntry
            {
                At = DateTimeOffset.UtcNow,
                Username = user.Identity?.Name ?? "?",
                Action = action,
                Details = details
            });
            await _db.SaveChangesAsync();
        }
        catch { /* журнал не должен ронять основное действие */ }
    }
}
