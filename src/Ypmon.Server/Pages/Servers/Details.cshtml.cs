using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Ypmon.Server.Data;
using Ypmon.Server.Services;
using Ypmon.Shared;

namespace Ypmon.Server.Pages.Servers;

public class DetailsModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly RemoteCommandService _commands;
    private readonly AuditService _audit;
    public DetailsModel(AppDbContext db, RemoteCommandService commands, AuditService audit) { _db = db; _commands = commands; _audit = audit; }

    [BindProperty(SupportsGet = true)] public int Id { get; set; }

    /// <summary>Режим «только информация» (переход с дашборда): прячем формы настройки.</summary>
    [BindProperty(SupportsGet = true)] public bool Info { get; set; }

    /// <summary>Порог свободного места на диске по умолчанию (для предзаполнения полей).</summary>
    public int DiskDefaultPercent => AvailabilityMonitor.DefaultDiskFreePercent;

    public MonitoredServer? Server { get; set; }
    public AgentReportDto? Report { get; set; }
    public List<Report> History { get; set; } = new();
    public List<AgentEvent> Events { get; set; } = new();
    public int OfflineThreshold { get; set; } = 300;

    /// <summary>Внешний адрес/порт сервера (для удалённых агентов) — из глобальных настроек.</summary>
    public string? ExternalAddress { get; set; }
    public int ExternalPort { get; set; } = 8081;

    /// <summary>Тренд объёма бэкапа по последним полным отчётам (хронологически).</summary>
    public record TrendPoint(string Label, long Size);
    public List<TrendPoint> BackupTrend { get; set; } = new();
    public long BackupTrendMax { get; set; } = 1;

    // Диски с машины агента и пороги свободного места (для тревог)
    public List<DiskStatusDto> Disks { get; set; } = new();
    public Dictionary<string, int> DiskThresholds { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // Пороги устаревания по папкам (имя папки → дней; 0/нет = наследует порог сервера).
    public Dictionary<string, int> FolderStaleDays { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // Здоровье физических накопителей (SMART) — показывается свёрнутым списком.
    public List<PhysicalDiskDto> PhysicalDisks { get; set; } = new();

    // Поля редактирования
    [BindProperty] public string Name { get; set; } = "";
    [BindProperty] public string? PhysicalAddress { get; set; }
    [BindProperty] public string? IpAddress { get; set; }
    [BindProperty] public string? ExternalIpAddress { get; set; }
    [BindProperty] public string? Description { get; set; }
    [BindProperty] public int BackupStaleDays { get; set; }
    public int DefaultBackupStaleDays { get; set; } = 1;
    public string? Message { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        await Load();
        if (Server is null) return NotFound();
        FillEditFields();
        return Page();
    }

    private async Task Load()
    {
        var settings = await _db.Settings.FirstOrDefaultAsync();
        OfflineThreshold = settings?.OfflineThresholdSeconds ?? 300;
        DefaultBackupStaleDays = settings?.DefaultBackupStaleDays ?? 1;
        ExternalAddress = settings?.ExternalAddress;
        ExternalPort = settings?.ExternalPort is > 0 and < 65536 ? settings.ExternalPort : 8081;
        Server = await _db.Servers.Include(s => s.Client).FirstOrDefaultAsync(s => s.Id == Id);
        if (Server is null) return;
        if (Server.LastReportJson is not null)
        {
            try { Report = JsonSerializer.Deserialize<AgentReportDto>(Server.LastReportJson); } catch { }
        }
        if (Server.LastDisksJson is not null)
        {
            try { Disks = JsonSerializer.Deserialize<List<DiskStatusDto>>(Server.LastDisksJson) ?? new(); } catch { }
        }
        if (Server.LastPhysicalDisksJson is not null)
        {
            try { PhysicalDisks = JsonSerializer.Deserialize<List<PhysicalDiskDto>>(Server.LastPhysicalDisksJson) ?? new(); } catch { }
        }
        DiskThresholds = AvailabilityMonitor.ParseThresholds(Server.DiskAlertsJson);
        FolderStaleDays = ReportIngestService.ParseFolderStaleDays(Server.FolderStaleDaysJson);
        // Сортируем по Id (автоинкремент ⇒ тот же порядок, что ReceivedAt): sqlite не умеет
        // ORDER BY по DateTimeOffset, а провайдер sqlite в проекте поддерживается.
        History = await _db.Reports.Where(r => r.ServerId == Id)
            .OrderByDescending(r => r.Id).Take(15).ToListAsync();

        // Тренд объёма бэкапа: последние 30 полных отчётов, объём = сумма доступных папок из payload.
        var trendRows = await _db.Reports.Where(r => r.ServerId == Id)
            .OrderByDescending(r => r.Id).Take(20)
            .Select(r => new { r.ReceivedAt, r.PayloadJson }).ToListAsync();
        trendRows.Reverse();   // хронологический порядок
        foreach (var r in trendRows)
        {
            long size = 0;
            try
            {
                var rep = JsonSerializer.Deserialize<AgentReportDto>(r.PayloadJson);
                if (rep is not null) size = rep.Folders.Where(f => f.Accessible).Sum(f => f.TotalSizeBytes);
            }
            catch { }
            BackupTrend.Add(new TrendPoint(r.ReceivedAt.UtcDateTime.ToString("dd.MM HH:mm"), size));
        }
        if (BackupTrend.Count > 0) BackupTrendMax = Math.Max(1, BackupTrend.Max(p => p.Size));
        Events = await _db.Events.Where(e => e.ServerId == Id)
            .OrderByDescending(e => e.Id).Take(50).ToListAsync();
    }

    public async Task<IActionResult> OnPostSaveAsync()
    {
        if (!User.CanEdit()) return Forbid();
        var s = await _db.Servers.FindAsync(Id);
        if (s is null) return NotFound();
        if (!string.IsNullOrWhiteSpace(Name)) s.Name = Name.Trim();
        s.PhysicalAddress = PhysicalAddress?.Trim();
        s.IpAddress = IpAddress?.Trim();
        s.ExternalIpAddress = string.IsNullOrWhiteSpace(ExternalIpAddress) ? null : ExternalIpAddress.Trim();
        s.Description = Description?.Trim();
        s.BackupStaleDays = Math.Max(0, BackupStaleDays);
        await _db.SaveChangesAsync();
        await _audit.LogAsync(User, "Изменён сервер", s.Name);
        await Load();
        FillEditFields();
        Message = "Сохранено";
        return Page();
    }

    public async Task<IActionResult> OnPostSaveMonitoringAsync(bool monitorOffline, bool monitorPing, int pingH, int pingM, int pingS)
    {
        if (!User.CanEdit()) return Forbid();
        var s = await _db.Servers.FindAsync(Id);
        if (s is null) return NotFound();

        s.MonitorOfflineAlert = monitorOffline;
        s.MonitorPing = monitorPing;
        s.PingIntervalSeconds = Math.Clamp(pingH * 3600 + pingM * 60 + pingS, 10, 7 * 24 * 3600);

        // Пороги по дискам: поля формы th_<имя диска>, значение — минимальный % свободного места.
        // Храним и 0 (явное «выключено»), иначе диск снова подпал бы под значение по умолчанию 15%.
        var dict = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in Request.Form.Keys.Where(k => k.StartsWith("th_", StringComparison.Ordinal)))
        {
            var disk = key[3..];
            if (int.TryParse(Request.Form[key], out var v))
                dict[disk] = Math.Clamp(v, 0, 99);
        }
        s.DiskAlertsJson = dict.Count == 0 ? null : JsonSerializer.Serialize(dict);

        // Пороги устаревания по папкам: поля формы fs_<имя папки>, значение — дней (0 = наследовать порог сервера).
        var folderDict = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in Request.Form.Keys.Where(k => k.StartsWith("fs_", StringComparison.Ordinal)))
        {
            var folder = key[3..];
            if (int.TryParse(Request.Form[key], out var v) && v > 0)
                folderDict[folder] = Math.Clamp(v, 1, 3650);
        }
        s.FolderStaleDaysJson = folderDict.Count == 0 ? null : JsonSerializer.Serialize(folderDict);

        // Чистим состояния тревог по дискам, для которых порог убрали, — иначе «висящая» тревога
        // не даст уведомлению сработать после повторного включения порога.
        try
        {
            var active = string.IsNullOrWhiteSpace(s.AlertDisksActiveJson)
                ? new List<string>()
                : JsonSerializer.Deserialize<List<string>>(s.AlertDisksActiveJson!) ?? new();
            var pruned = active.Where(d => dict.ContainsKey(d)).ToList();
            s.AlertDisksActiveJson = pruned.Count == 0 ? null : JsonSerializer.Serialize(pruned);
        }
        catch { s.AlertDisksActiveJson = null; }

        // Выключили мониторинг — сбрасываем состояние, чтобы после включения не «зависла» старая тревога.
        if (!monitorOffline) s.AlertOfflineActive = false;
        if (!monitorPing) s.AlertPingActive = false;

        await _db.SaveChangesAsync();
        await _audit.LogAsync(User, "Изменён мониторинг сервера", s.Name);
        await Load();
        FillEditFields();
        Message = "Настройки мониторинга сохранены";
        return Page();
    }

    private void FillEditFields()
    {
        if (Server is null) return;
        Name = Server.Name;
        PhysicalAddress = Server.PhysicalAddress;
        IpAddress = Server.IpAddress;
        ExternalIpAddress = Server.ExternalIpAddress;
        Description = Server.Description;
        BackupStaleDays = Server.BackupStaleDays;
    }

    // Подтверждение «архив действительно стал меньше» — снимаем тревогу и принимаем новый объём за опорный.
    public async Task<IActionResult> OnPostAckShrinkAsync()
    {
        if (!User.CanEdit()) return Forbid();
        var s = await _db.Servers.FindAsync(Id);
        if (s is not null)
        {
            // Опорный файл уже двигается при приёме отчёта, так что следующий новый файл
            // сравнится с текущим. Здесь только снимаем тревогу.
            s.BackupShrinkActive = false;
            s.BackupShrinkFolder = null;
            await _db.SaveChangesAsync();
            await _audit.LogAsync(User, "Подтверждено уменьшение файла бэкапа", s.Name);
        }
        await Load();
        FillEditFields();
        Message = "Принято: новый объём бэкапа считается корректным.";
        return Page();
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        if (!User.CanEdit()) return Forbid();
        var s = await _db.Servers.FindAsync(Id);
        if (s is not null)
        {
            var clientId = s.ClientId;
            var name = s.Name;
            _db.Servers.Remove(s);
            await _db.SaveChangesAsync();
            await _audit.LogAsync(User, "Удалён сервер", name);
            return RedirectToPage("/Clients/Details", new { id = clientId });
        }
        return RedirectToPage("/Index");
    }

    // Кнопка «Запросить отчёт» — единственная команда, доступная роли «Мониторинг»:
    // она ничего не меняет на машине клиента, только просит прислать данные.
    public async Task<IActionResult> OnPostRequestReportAsync()
        => await IssueAsync(AgentCommand.RequestReport,
            "Запрос отправлен. Агент пришлёт отчёт при следующей связи (обычно в течение 1–2 минут).",
            requireEdit: false);

    // Кнопка «Обновить агента» (принудительное обновление).
    public async Task<IActionResult> OnPostRequestUpdateAsync()
        => await IssueAsync(AgentCommand.UpdateAgent,
            "Команда на обновление отправлена. Агент проверит и установит обновление при следующей связи " +
            "(обычно в течение 1–2 минут); служба перезапустится сама.");

    // Кнопка «Перезагрузить сервер».
    public async Task<IActionResult> OnPostRequestRebootAsync()
        => await IssueAsync(AgentCommand.RebootMachine,
            "Команда на перезагрузку отправлена. Агент получит её при следующей связи и перезагрузит машину " +
            "с отсрочкой в 1 минуту.");

    // Кнопка «Отменить команду».
    public async Task<IActionResult> OnPostCancelCommandAsync()
    {
        if (!User.CanEdit()) return Forbid();
        await _commands.CancelAsync(Id);
        await Load();
        FillEditFields();
        await _audit.LogAsync(User, "Отменена команда агенту", Server?.Name);
        Message = "Команда снята с очереди.";
        return Page();
    }

    /// <summary>Общая часть выдачи команды: права, постановка в очередь, журнал.</summary>
    private async Task<IActionResult> IssueAsync(AgentCommand command, string message, bool requireEdit = true)
    {
        if (requireEdit && !User.CanEdit()) return Forbid();

        await _commands.IssueAsync(Id, command, User.Identity?.Name ?? "?");
        await Load();
        FillEditFields();
        await _audit.LogAsync(User, "Команда агенту: " + RemoteCommandService.Title(command), Server?.Name);
        Message = message;
        return Page();
    }

    public async Task<IActionResult> OnPostRegenKeyAsync()
    {
        if (!User.CanEdit()) return Forbid();
        var s = await _db.Servers.FindAsync(Id);
        if (s is not null)
        {
            s.ApiKey = Services.PasswordHasher.NewApiKey();
            await _db.SaveChangesAsync();
            await _audit.LogAsync(User, "Перевыпущен API-ключ сервера", s.Name);
        }
        return RedirectToPage(new { id = Id });
    }
}
