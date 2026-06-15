using System.Net;
using System.Net.Http;
using System.Net.Mail;
using System.Text;
using Ypmon.Server.Data;

namespace Ypmon.Server.Services;

/// <summary>Отправка оповещений (Telegram / e-mail) при проблемах на серверах.</summary>
public class AlertService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<AlertService> _log;

    public AlertService(IHttpClientFactory httpFactory, ILogger<AlertService> log)
    {
        _httpFactory = httpFactory;
        _log = log;
    }

    public async Task SendAsync(ServerSettings s, string subject, string body)
    {
        if (!s.AlertsEnabled) return;

        if (s.TelegramEnabled && !string.IsNullOrWhiteSpace(s.TelegramBotToken) && !string.IsNullOrWhiteSpace(s.TelegramChatId))
        {
            try { await SendTelegramAsync(s.TelegramBotToken!, s.TelegramChatId!, $"*{subject}*\n{body}", s.TelegramProxyUrl); }
            catch (Exception ex) { _log.LogWarning(ex, "Не удалось отправить Telegram-оповещение"); }
        }

        if (s.EmailEnabled && !string.IsNullOrWhiteSpace(s.SmtpHost) && !string.IsNullOrWhiteSpace(s.EmailTo))
        {
            try { await SendEmailAsync(s, subject, body); }
            catch (Exception ex) { _log.LogWarning(ex, "Не удалось отправить e-mail оповещение"); }
        }
    }

    private async Task SendTelegramAsync(string token, string chatId, string text, string? proxyUrl)
    {
        HttpClient http;
        HttpClientHandler? handler = null;
        if (!string.IsNullOrWhiteSpace(proxyUrl))
        {
            handler = new HttpClientHandler
            {
                Proxy = new System.Net.WebProxy(proxyUrl),
                UseProxy = true
            };
            http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        }
        else
        {
            http = _httpFactory.CreateClient();
        }

        try
        {
            var url = $"https://api.telegram.org/bot{token}/sendMessage";
            var payload = new Dictionary<string, string>
            {
                ["chat_id"] = chatId,
                ["text"] = text,
                ["parse_mode"] = "Markdown"
            };
            var resp = await http.PostAsync(url, new FormUrlEncodedContent(payload));
            resp.EnsureSuccessStatusCode();
        }
        finally
        {
            if (handler is not null) { http.Dispose(); handler.Dispose(); }
        }
    }

    private async Task SendEmailAsync(ServerSettings s, string subject, string body)
    {
        using var msg = new MailMessage
        {
            From = new MailAddress(s.EmailFrom ?? s.SmtpUser ?? "ypmon@localhost"),
            Subject = subject,
            Body = body,
            BodyEncoding = Encoding.UTF8,
            SubjectEncoding = Encoding.UTF8
        };
        foreach (var to in (s.EmailTo ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            msg.To.Add(to);

        using var client = new SmtpClient(s.SmtpHost, s.SmtpPort) { EnableSsl = s.SmtpUseSsl };
        if (!string.IsNullOrWhiteSpace(s.SmtpUser))
            client.Credentials = new NetworkCredential(s.SmtpUser, s.SmtpPassword);
        await client.SendMailAsync(msg);
    }
}
