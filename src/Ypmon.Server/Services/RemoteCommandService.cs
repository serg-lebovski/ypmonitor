using Ypmon.Server.Data;
using Ypmon.Shared;

namespace Ypmon.Server.Services;

/// <summary>
/// Команды сервера агенту.
///
/// Принципы, на которых это построено (менять их нельзя — на них держится безопасность):
///  • Агент не слушает порты и не принимает входящих соединений. Он сам видит команду
///    в ответе на свой heartbeat.
///  • Список команд закрытый и зашит в агенте (<see cref="AgentCommand"/>). С сервера едет
///    только НОМЕР команды — ни путей, ни аргументов, ни строк для исполнения. Поэтому
///    компрометация сервера не даёт выполнения произвольного кода на машинах клиентов.
///  • У каждой команды уникальный номер: агент запоминает последний выполненный (на диске,
///    переживает перезагрузку) и повторно её не исполняет. Иначе перехваченный ответ можно
///    было бы проиграть заново, а перезагрузка ушла бы в бесконечный цикл.
///  • У команды есть срок годности. Агент, вернувшийся из офлайна через сутки, её не выполнит.
/// </summary>
public sealed class RemoteCommandService
{
    private readonly AppDbContext _db;
    public RemoteCommandService(AppDbContext db) => _db = db;

    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(ReportAckDto.CommandTtlMinutes);

    /// <summary>Человекочитаемое название команды (для интерфейса и журнала).</summary>
    public static string Title(AgentCommand c) => c switch
    {
        AgentCommand.RequestReport => "Запросить отчёт",
        AgentCommand.UpdateAgent => "Обновить агента",
        AgentCommand.RebootMachine => "Перезагрузить сервер",
        _ => "—"
    };

    /// <summary>Поставить команду в очередь агенту. Перебивает предыдущую невыполненную.</summary>
    public async Task<bool> IssueAsync(int serverId, AgentCommand command, string issuedBy)
    {
        var s = await _db.Servers.FindAsync(serverId);
        if (s is null || command == AgentCommand.None) return false;

        s.PendingCommand = (int)command;
        // Номер — тики UTC: монотонно растёт, уникален в пределах сервера.
        s.PendingCommandId = DateTimeOffset.UtcNow.Ticks;
        s.PendingCommandAt = DateTimeOffset.UtcNow;
        s.PendingCommandBy = issuedBy;

        // Совместимость с агентами до 2.7: они читают только эти флаги.
        // Команды «перезагрузить» у них нет — старый агент её просто не увидит.
        if (command == AgentCommand.RequestReport) s.ReportRequested = true;
        if (command == AgentCommand.UpdateAgent) s.UpdateRequested = true;

        await _db.SaveChangesAsync();
        return true;
    }

    /// <summary>Снять невыполненную команду (кнопка «отменить»).</summary>
    public async Task CancelAsync(int serverId)
    {
        var s = await _db.Servers.FindAsync(serverId);
        if (s is null) return;
        Clear(s);
        await _db.SaveChangesAsync();
    }

    /// <summary>Текущая команда сервера, если она есть и ещё не протухла.</summary>
    public static AgentCommand PendingOf(MonitoredServer s)
        => IsAlive(s) ? (AgentCommand)s.PendingCommand : AgentCommand.None;

    private static bool IsAlive(MonitoredServer s)
        => s.PendingCommand != 0 && s.PendingCommandAt is { } at && DateTimeOffset.UtcNow - at <= Ttl;

    /// <summary>
    /// Кладёт команду в ответ агенту. Просроченные команды снимаются молча.
    /// Вызывающий обязан сохранить изменения (SaveChanges).
    /// </summary>
    public void FillAck(MonitoredServer server, ReportAckDto ack)
    {
        if (server.PendingCommand != 0 && !IsAlive(server))
        {
            Clear(server);   // протухла — снимаем, агенту не отдаём
            return;
        }
        if (server.PendingCommand == 0) return;

        ack.CommandId = server.PendingCommandId;
        ack.Command = (AgentCommand)server.PendingCommand;
        ack.CommandIssuedAt = server.PendingCommandAt;

        // Для агентов до 2.7 — старые флаги. Обновление у них одноразовое:
        // сбрасываем сразу при выдаче, иначе агент зациклится на переустановке.
        ack.ReportRequested = server.ReportRequested;
        if (server.UpdateRequested)
        {
            ack.UpdateRequested = true;
            server.UpdateRequested = false;
        }
    }

    /// <summary>
    /// Агент прислал номер выполненной команды — снимаем её с очереди.
    /// Вызывающий обязан сохранить изменения (SaveChanges).
    /// </summary>
    public void MarkExecuted(MonitoredServer server, long lastCommandId)
    {
        if (lastCommandId == 0 || lastCommandId == server.LastExecutedCommandId) return;

        server.LastExecutedCommandId = lastCommandId;
        server.LastExecutedCommandAt = DateTimeOffset.UtcNow;
        if (server.PendingCommandId == lastCommandId) Clear(server);
    }

    /// <summary>Полный отчёт получен — если ждали именно его, запрос закрыт (для старых агентов).</summary>
    public void MarkReportReceived(MonitoredServer server)
    {
        server.ReportRequested = false;
        if ((AgentCommand)server.PendingCommand == AgentCommand.RequestReport) Clear(server);
    }

    private static void Clear(MonitoredServer s)
    {
        s.PendingCommand = 0;
        s.PendingCommandId = 0;
        s.PendingCommandAt = null;
        s.PendingCommandBy = null;
        s.ReportRequested = false;
        s.UpdateRequested = false;
    }
}
