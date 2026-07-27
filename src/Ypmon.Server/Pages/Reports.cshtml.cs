using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Ypmon.Server.Data;
using Ypmon.Server.Services;

namespace Ypmon.Server.Pages;

public class ReportsModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly ClientReportPdfService _pdf;
    private readonly AuditService _audit;
    public ReportsModel(AppDbContext db, ClientReportPdfService pdf, AuditService audit)
    {
        _db = db; _pdf = pdf; _audit = audit;
    }

    public List<Client> Clients { get; set; } = new();

    public async Task OnGetAsync()
    {
        Clients = await _db.Clients.Include(c => c.Servers).OrderBy(c => c.Name).ToListAsync();
        foreach (var c in Clients)
            c.Servers = c.Servers.OrderBy(s => s.Name).ToList();
    }

    /// <summary>
    /// Отчёт по обслуживанию для клиента (PDF) — по всем его серверам за выбранный период.
    /// Отдаём файл сразу на скачивание: хранить его на сервере незачем.
    /// </summary>
    public async Task<IActionResult> OnPostPdfAsync(int clientId, string? period)
    {
        if (!User.CanEdit()) return Forbid();

        var omskNow = DateTimeOffset.UtcNow.AddHours(6);
        // Границы периода считаем по Омску, но храним в UTC — как и всё остальное время в базе.
        var (from, to) = period switch
        {
            "prev-month" => (Month(omskNow.AddMonths(-1)), Month(omskNow).AddSeconds(-1)),
            "this-month" => (Month(omskNow), omskNow),
            _ => (omskNow.AddDays(-30), omskNow),
        };

        var result = await _pdf.BuildAsync(clientId, from.AddHours(-6), to.AddHours(-6));
        if (result is null) return NotFound();

        await _audit.LogAsync(User, "Сформирован отчёт по клиенту", $"клиент #{clientId}, период {period ?? "30 дней"}");
        return File(result.Value.pdf, "application/pdf", result.Value.fileName);

        static DateTimeOffset Month(DateTimeOffset t) => new(t.Year, t.Month, 1, 0, 0, 0, t.Offset);
    }
}
