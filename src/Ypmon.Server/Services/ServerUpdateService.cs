using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ypmon.Server.Services;

/// <summary>
/// Статус самообновления, который пишет хостовый воркер (deploy/ypmon-updater.sh) в status.json.
/// </summary>
public sealed class ServerUpdateStatus
{
    [JsonPropertyName("state")] public string State { get; set; } = "unknown";
    [JsonPropertyName("updateAvailable")] public bool UpdateAvailable { get; set; }
    [JsonPropertyName("behind")] public int Behind { get; set; }
    [JsonPropertyName("currentShort")] public string? CurrentShort { get; set; }
    [JsonPropertyName("remoteShort")] public string? RemoteShort { get; set; }
    [JsonPropertyName("lastChecked")] public DateTimeOffset? LastChecked { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }

    public bool IsBusy => State is "checking" or "updating";
}

/// <summary>
/// Обмен с хостовым воркером самообновления через смонтированную папку-канал (по умолчанию /app/update).
/// Веб-приложение только пишет запрос (check/apply) и читает статус — доступа к docker/git у него нет.
/// </summary>
public sealed class ServerUpdateService
{
    private readonly string _dir;
    private static readonly JsonSerializerOptions J = new() { PropertyNameCaseInsensitive = true };

    public ServerUpdateService(IConfiguration cfg)
    {
        _dir = cfg["Server:UpdateDir"] ?? "/app/update";
    }

    /// <summary>Самообновление доступно, только если папка-канал смонтирована воркером.</summary>
    public bool Configured => Directory.Exists(_dir);

    public ServerUpdateStatus? ReadStatus()
    {
        try
        {
            var f = Path.Combine(_dir, "status.json");
            if (!File.Exists(f)) return null;
            return JsonSerializer.Deserialize<ServerUpdateStatus>(File.ReadAllText(f), J);
        }
        catch { return null; }
    }

    public string[] ReadChangelog()
    {
        try
        {
            var f = Path.Combine(_dir, "changelog.txt");
            if (!File.Exists(f)) return Array.Empty<string>();
            return File.ReadAllLines(f).Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
        }
        catch { return Array.Empty<string>(); }
    }

    public void RequestCheck() => Write("check");
    public void RequestApply() => Write("apply");

    private void Write(string cmd)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "request"), cmd);
    }

    /// <summary>
    /// Ждёт, пока воркер обработает запрос и обновит status.json (lastChecked станет новее since),
    /// не дольше timeout. Возвращает свежий статус (или последний доступный).
    /// </summary>
    public async Task<ServerUpdateStatus?> WaitForFreshStatusAsync(DateTimeOffset since, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var s = ReadStatus();
            if (s is { IsBusy: false } && s.LastChecked is { } lc && lc > since)
                return s;
            await Task.Delay(400);
        }
        return ReadStatus();
    }
}
