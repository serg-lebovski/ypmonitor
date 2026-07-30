using System.Text;
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

    /// <summary>
    /// CSV-выгрузка таблицы адресов клиентов. Разделитель — ";", а не ",": так CSV открывается
    /// в Excel с русской локалью без плясок с настройками импорта. BOM в начале — чтобы Excel
    /// понял, что файл в UTF-8, и не превратил кириллицу в кракозябры.
    /// </summary>
    public async Task<IActionResult> OnGetAddressesCsvAsync()
    {
        var clients = await _db.Clients.Include(c => c.Servers).OrderBy(c => c.Name).ToListAsync();

        var sb = new StringBuilder();
        sb.Append("Клиент;Сервер;Физический адрес;Локальный IP;Внешний IP;Провайдер\r\n");
        foreach (var c in clients)
            foreach (var s in c.Servers.OrderBy(x => x.Name))
                sb.Append(string.Join(';', new[] { c.Name, s.Name, s.PhysicalAddress, s.IpAddress, s.ExternalIpAddress, s.IspProvider }
                    .Select(CsvEscape))).Append("\r\n");

        var bom = new byte[] { 0xEF, 0xBB, 0xBF };
        var body = Encoding.UTF8.GetBytes(sb.ToString());
        var bytes = new byte[bom.Length + body.Length];
        bom.CopyTo(bytes, 0);
        body.CopyTo(bytes, bom.Length);

        var fileName = $"ypmon-addresses-{DateTimeOffset.UtcNow.AddHours(6):yyyy-MM-dd}.csv";
        return File(bytes, "text/csv", fileName);

        static string CsvEscape(string? v)
        {
            v ??= "";
            return v.IndexOfAny(new[] { ';', '"', '\n', '\r' }) >= 0
                ? "\"" + v.Replace("\"", "\"\"") + "\""
                : v;
        }
    }
}
