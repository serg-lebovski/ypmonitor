using System.Net;
using System.Net.Http;
using System.Net.Mail;
using System.Text;
using Ypmon.Server.Data;

namespace Ypmon.Server.Services;

/// <summary>Отправка оповещений (Telegram / e-mail) при проблемах на серверах.</summary>
public class AlertService
{
    private readonly TelegramService _tg;
    private readonly ILogger<AlertService> _log;

    public AlertService(TelegramService tg, ILogger<AlertService> log)
    {
        _tg = tg;
        _log = log;
    }

    public async Task SendAsync(ServerSettings s, string subject, string body)
    {
        if (!s.AlertsEnabled) return;

        if (s.TelegramEnabled && !string.IsNullOrWhiteSpace(s.TelegramBotToken) && !string.IsNullOrWhiteSpace(s.TelegramChatId))
        {
            try
            {
                var text = $"<b>{System.Net.WebUtility.HtmlEncode(subject)}</b>\n{System.Net.WebUtility.HtmlEncode(body)}";
                await _tg.SendMessageAsync(s, s.TelegramChatId!, text);
            }
            catch (Exception ex) { _log.LogWarning(ex, "Не удалось отправить Telegram-оповещение"); }
        }

        if (s.EmailEnabled && !string.IsNullOrWhiteSpace(s.SmtpHost) && !string.IsNullOrWhiteSpace(s.EmailTo))
        {
            try { await SendEmailAsync(s, subject, body); }
            catch (Exception ex) { _log.LogWarning(ex, "Не удалось отправить e-mail оповещение"); }
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
