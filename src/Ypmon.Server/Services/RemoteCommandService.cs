using Ypmon.Server.Data;
using Ypmon.Shared;

namespace Ypmon.Server.Services;

/// <summary>
/// ==========================================================================
/// [YPMON-REMOTE-CMD] Команды сервера агенту — ВЕСЬ ФАЙЛ ВЫРЕЗАТЬ ПОСЛЕ РЕЛИЗА.
///
/// По изначальному замыслу агент строго исходящий: только шлёт отчёты и не
/// получает никаких команд. Здесь собрано ВСЁ временное исключение из правила —
/// флаги, которые агент сам вычитывает в ответе на heartbeat:
///   • ReportRequested — «пришли полный отчёт сейчас» (кнопка «Запросить отчёт»);
///   • UpdateRequested — «проверь и установи обновление сейчас» (кнопка «Обновить агента»).
///
/// Как вырезать: удалить этот файл и все места с пометкой [YPMON-REMOTE-CMD]
/// (поиск по коду: YPMON-REMOTE-CMD) — контракты, поля сущности, миграции,
/// кнопки в UI и обработчик на стороне агента (RemoteCommandHandler).
/// ==========================================================================
/// </summary>
public sealed class RemoteCommandService
{
    private readonly AppDbContext _db;
    public RemoteCommandService(AppDbContext db) => _db = db;

    /// <summary>[YPMON-REMOTE-CMD] Поставить флаг «нужен полный отчёт».</summary>
    public async Task<bool> RequestReportAsync(int serverId)
    {
        var s = await _db.Servers.FindAsync(serverId);
        if (s is null) return false;
        s.ReportRequested = true;
        await _db.SaveChangesAsync();
        return true;
    }

    /// <summary>[YPMON-REMOTE-CMD] Поставить флаг «принудительно обновись».</summary>
    public async Task<bool> RequestUpdateAsync(int serverId)
    {
        var s = await _db.Servers.FindAsync(serverId);
        if (s is null) return false;
        s.UpdateRequested = true;
        await _db.SaveChangesAsync();
        return true;
    }

    /// <summary>
    /// [YPMON-REMOTE-CMD] Заполняет флаги-команды в ack ответа на heartbeat.
    /// UpdateRequested — одноразовый: сбрасывается сразу при выдаче (агент дальше действует сам).
    /// ReportRequested сбрасывается позже — когда полный отчёт реально пришёл (MarkReportReceived).
    /// Вызывающий обязан сохранить изменения (SaveChanges).
    /// </summary>
    public void FillAck(MonitoredServer server, ReportAckDto ack)
    {
        ack.ReportRequested = server.ReportRequested;
        if (server.UpdateRequested)
        {
            ack.UpdateRequested = true;
            server.UpdateRequested = false;
        }
    }

    /// <summary>[YPMON-REMOTE-CMD] Полный отчёт получен — запрос отчёта выполнен.</summary>
    public void MarkReportReceived(MonitoredServer server) => server.ReportRequested = false;
}
