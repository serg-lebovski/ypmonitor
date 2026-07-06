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
    /// Прокси для обращения к Telegram (HTTP Bot API): WireGuard (userspace SOCKS5), если поднят.
    /// MTProto-прокси к HTTP Bot API неприменим и здесь не используется.
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

    private async Task<JsonElement?> CallAsync(ServerSettings s, string method, Dictionary<string, string> args)
    {
        if (string.IsNullOrWhiteSpace(s.TelegramBotToken)) return null;
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
                _log.LogWarning("Telegram {Method}: {Desc}", method,
                    root.TryGetProperty("description", out var d) ? d.GetString() : json);
                return null;
            }
            // Клонируем результат, т.к. doc будет освобождён.
            return root.TryGetProperty("result", out var result) ? result.Clone() : (JsonElement?)null;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Telegram {Method} — ошибка вызова", method);
            return null;
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
