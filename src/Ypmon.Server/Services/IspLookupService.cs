using System.Text.Json;

namespace Ypmon.Server.Services;

/// <summary>
/// Определение интернет-провайдера по внешнему IP через бесплатный API ipwho.is (без ключа).
/// Только по кнопке на карточке сервера — не вызывается автоматически при каждом заходе на
/// страницу. Если сервис недоступен или не смог определить провайдера, возвращает null —
/// администратор всегда может вписать название провайдера вручную в том же поле.
/// </summary>
public class IspLookupService
{
    private readonly HttpClient _http;
    public IspLookupService(HttpClient http)
    {
        _http = http;
        _http.Timeout = TimeSpan.FromSeconds(6);
    }

    public async Task<string?> LookupAsync(string ip)
    {
        try
        {
            using var resp = await _http.GetAsync($"https://ipwho.is/{Uri.EscapeDataString(ip)}");
            if (!resp.IsSuccessStatusCode) return null;

            using var stream = await resp.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);
            var root = doc.RootElement;
            if (!root.TryGetProperty("success", out var okEl) || !okEl.GetBoolean()) return null;
            if (!root.TryGetProperty("connection", out var conn)) return null;

            var isp = conn.TryGetProperty("isp", out var ispEl) ? ispEl.GetString() : null;
            var org = conn.TryGetProperty("org", out var orgEl) ? orgEl.GetString() : null;
            var result = string.IsNullOrWhiteSpace(isp) ? org : isp;
            return string.IsNullOrWhiteSpace(result) ? null : result.Trim();
        }
        catch
        {
            return null;
        }
    }
}
