using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Ypmon.Server.Data;

namespace Ypmon.Server.Services;

/// <summary>Клиент Telegram Bot API: сообщения, темы (forum topics), определение chat_id.</summary>
public class TelegramService
{
    private readonly WireGuardProxyService _wg;
    private readonly ILogger<TelegramService> _log;

    public TelegramService(WireGuardProxyService wg, ILogger<TelegramService> log)
    {
        _wg = wg; _log = log;
    }

    /// <summary>
    /// Прокси для обращения к Telegram (HTTP Bot API): WireGuard/AmneziaWG (userspace SOCKS5), если поднят.
    /// </summary>
    public string? ResolveProxy(ServerSettings s)
    {
        if (s.WireGuardEnabled && _wg.IsRunning) return _wg.SocksProxyUrl;
        return null;
    }

    private HttpClient CreateClient(ServerSettings s)
    {
        var proxy = ResolveProxy(s);
        if (!string.IsNullOrWhiteSpace(proxy))
        {
            var handler = new HttpClientHandler { Proxy = new WebProxy(proxy), UseProxy = true };
            return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(40) };
        }
        return new HttpClient { Timeout = TimeSpan.FromSeconds(40) };
    }

    /// <summary>
    /// Ограничитель темпа отправки. Telegram разрешает боту ~20 сообщений в минуту в одну группу;
    /// при превышении отвечает 429 «Too Many Requests». Раньше такие сообщения просто терялись —
    /// ежедневный отчёт по сотне клиентов доходил лишь первыми несколькими сообщениями.
    /// </summary>
    private static readonly SemaphoreSlim SendGate = new(1, 1);
    private static DateTimeOffset _lastSendAt = DateTimeOffset.MinValue;
    private static readonly TimeSpan MinSendInterval = TimeSpan.FromMilliseconds(3200);   // ~19 сообщений/мин

    /// <summary>Выдерживает паузу между отправками сообщений, чтобы не упираться в лимит Telegram.</summary>
    private static async Task PaceAsync()
    {
        await SendGate.WaitAsync();
        try
        {
            var wait = _lastSendAt + MinSendInterval - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait);
            _lastSendAt = DateTimeOffset.UtcNow;
        }
        finally { SendGate.Release(); }
    }

    private async Task<JsonElement?> CallAsync(ServerSettings s, string method, Dictionary<string, string> args)
    {
        if (string.IsNullOrWhiteSpace(s.TelegramBotToken)) return null;

        // Темп выдерживаем только для отправки сообщений — служебные вызовы не ограничиваем.
        var isSend = method is "sendMessage" or "createForumTopic";

        for (var attempt = 1; attempt <= 4; attempt++)
        {
            if (isSend) await PaceAsync();

            var (result, retryAfter) = await CallOnceAsync(s, method, args);
            if (retryAfter is null) return result;   // успех или ошибка, которую повтор не исправит

            // 429: Telegram сам говорит, сколько ждать. Ждём и повторяем — иначе сообщение теряется.
            var delay = TimeSpan.FromSeconds(Math.Clamp(retryAfter.Value, 1, 120));
            _log.LogWarning("Telegram {Method}: лимит частоты, повтор через {Sec} с (попытка {Attempt}/4)",
                method, delay.TotalSeconds, attempt);
            await Task.Delay(delay);
        }

        _log.LogWarning("Telegram {Method}: не отправлено — лимит частоты не снялся за 4 попытки", method);
        return null;
    }

    /// <summary>Один вызов API. Возвращает результат и, если пришёл 429, — сколько секунд ждать.</summary>
    private async Task<(JsonElement? result, int? retryAfter)> CallOnceAsync(
        ServerSettings s, string method, Dictionary<string, string> args)
    {
        using var http = CreateClient(s);
        var url = $"https://api.telegram.org/bot{s.TelegramBotToken}/{method}";
        try
        {
            var resp = await http.PostAsync(url, new FormUrlEncodedContent(args));
            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
            {
                // Лимит частоты — извлекаем parameters.retry_after и сообщаем вызывающему.
                if (root.TryGetProperty("error_code", out var ec) && ec.TryGetInt32(out var code) && code == 429)
                {
                    var after = 3;
                    if (root.TryGetProperty("parameters", out var p) &&
                        p.TryGetProperty("retry_after", out var ra) && ra.TryGetInt32(out var rav))
                        after = rav;
                    return (null, after);
                }
                _log.LogWarning("Telegram {Method}: {Desc}", method,
                    root.TryGetProperty("description", out var d) ? d.GetString() : json);
                return (null, null);
            }
            // Клонируем результат, т.к. doc будет освобождён.
            return (root.TryGetProperty("result", out var result) ? result.Clone() : (JsonElement?)null, null);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Telegram {Method} — ошибка вызова", method);
            return (null, null);
        }
    }

    public async Task<(bool ok, string? error)> SendMessageAsync(ServerSettings s, string chatId, string text,
        long? threadId = null, bool disablePreview = false)
    {
        var args = new Dictionary<string, string>
        {
            ["chat_id"] = chatId,
            ["text"] = text,
            ["parse_mode"] = "HTML",
            ["disable_web_page_preview"] = disablePreview ? "true" : "false"
        };
        if (threadId is { } t) args["message_thread_id"] = t.ToString();
        var res = await CallAsync(s, "sendMessage", args);
        return (res is not null, res is null ? "см. журнал сервера" : null);
    }

    /// <summary>Создаёт тему (forum topic) и возвращает её message_thread_id.</summary>
    public async Task<long?> CreateForumTopicAsync(ServerSettings s, string chatId, string name)
    {
        var res = await CallAsync(s, "createForumTopic", new Dictionary<string, string>
        {
            ["chat_id"] = chatId,
            ["name"] = name
        });
        if (res is { } r && r.TryGetProperty("message_thread_id", out var id) && id.TryGetInt64(out var tid))
            return tid;
        return null;
    }

    /// <summary>Список недавних чатов (для определения chat_id группы) через getUpdates.</summary>
    public async Task<string> DiscoverChatsAsync(ServerSettings s)
    {
        var res = await CallAsync(s, "getUpdates", new Dictionary<string, string> { ["timeout"] = "0" });
        if (res is not { ValueKind: JsonValueKind.Array } arr)
            return "Не удалось получить обновления. Проверьте токен/прокси и напишите что-нибудь в группу.";

        var seen = new Dictionary<string, string>();
        foreach (var upd in arr.EnumerateArray())
        {
            foreach (var key in new[] { "message", "channel_post", "my_chat_member" })
            {
                if (upd.TryGetProperty(key, out var msg) && msg.TryGetProperty("chat", out var chat)
                    && chat.TryGetProperty("id", out var id))
                {
                    var title = chat.TryGetProperty("title", out var t) ? t.GetString()
                              : (chat.TryGetProperty("username", out var u) ? u.GetString() : "(чат)");
                    seen[id.GetRawText()] = $"{title} [{(chat.TryGetProperty("type", out var ty) ? ty.GetString() : "")}]";
                }
            }
        }
        if (seen.Count == 0)
            return "Обновлений нет. Добавьте бота в группу, включите Темы, напишите в группе сообщение и повторите.";
        return "Найденные чаты (chat_id — название):\n" + string.Join("\n", seen.Select(kv => $"{kv.Key} — {kv.Value}"));
    }
}
