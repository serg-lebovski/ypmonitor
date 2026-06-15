using Ypmon.Shared;

namespace Ypmon.Agent.Services;

/// <summary>
/// Снапшот состояния агента, который рабочая служба пишет в snapshot.json,
/// а окно настроек читает (без сетевого обмена и без локального порта).
/// </summary>
public class AgentSnapshot
{
    public DateTimeOffset? LastReportAt { get; set; }
    public bool LastReportAccepted { get; set; }
    public string? LastReportMessage { get; set; }
    public string? ResolvedClientName { get; set; }
    public string? ResolvedServerName { get; set; }
    public string? LastError { get; set; }

    /// <summary>Последний сформированный отчёт (задания, диски, доступность).</summary>
    public AgentReportDto? LastReport { get; set; }
}
