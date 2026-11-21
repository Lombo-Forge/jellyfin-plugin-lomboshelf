using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LomboShelf.Services;

/// <summary>Farewell's answer: HTTP status (0 when no usable answer came) and its message, cleaned.</summary>
public sealed record FarewellReply(int Status, string? Message);

public interface IFarewellClient
{
    Task<FarewellReply> KeepAsync(string baseUrl, string token, string itemIdHex, CancellationToken ct);
}

/// <summary>Calls Farewell's keep route. The token is only ever put in the Authorization header of that one request.</summary>
public sealed class FarewellClient : IFarewellClient
{
    public const int MaxResponseBytes = 64 * 1024;
    private readonly HttpClient _http;
    private readonly ILogger<FarewellClient>? _log;

    public FarewellClient(ILogger<FarewellClient>? log = null)
        : this(new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(10) }, log)
    {
    }

    /// <summary>For tests: the caller supplies the handler (no network).</summary>
    public FarewellClient(HttpClient http, ILogger<FarewellClient>? log = null)
    {
        _http = http;
        _http.Timeout = TimeSpan.FromSeconds(10);
        _log = log;
    }

    public async Task<FarewellReply> KeepAsync(string baseUrl, string token, string itemIdHex, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl.TrimEnd('/') + "/v1/keep");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Content = new StringContent(JsonSerializer.Serialize(new Dictionary<string, string> { ["title"] = itemIdHex }), Encoding.UTF8);
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            var status = (int)resp.StatusCode;
            if (status != 200 && status != 422)
            {
                return new FarewellReply(status, null);
            }

            var body = await ReadLimitedAsync(resp, ct).ConfigureAwait(false);
            return body is null ? new FarewellReply(0, null) : new FarewellReply(status, MessageFrom(body));
        }
        catch (Exception ex)
        {
            // Only the exception type: its message can hold the address.
            _log?.LogWarning("The keep request to Farewell did not complete ({Type}).", ex.GetType().Name);
            return new FarewellReply(0, null);
        }
    }

    /// <summary>The body, read up to <see cref="MaxResponseBytes"/>; null when it is longer.</summary>
    public static async Task<byte[]?> ReadLimitedAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new byte[MaxResponseBytes + 1];
        var total = 0;
        int n;
        while (total < buffer.Length && (n = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct).ConfigureAwait(false)) > 0)
        {
            total += n;
        }

        return total > MaxResponseBytes ? null : buffer.AsSpan(0, total).ToArray();
    }

    /// <summary>The "message" text of a JSON answer, cleaned; null when there is none.</summary>
    public static string? MessageFrom(ReadOnlySpan<byte> body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body.ToArray());
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
            {
                return Clean(m.GetString());
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    /// <summary>Printable characters only (control characters and line breaks become spaces), trimmed, at most 200.</summary>
    public static string? Clean(string? text)
    {
        if (text is null)
        {
            return null;
        }

        var sb = new StringBuilder();
        foreach (var ch in text)
        {
            sb.Append(char.IsControl(ch) || char.IsSurrogate(ch) ? ' ' : ch);
        }

        var s = sb.ToString().Trim();
        if (s.Length > 200)
        {
            s = s.Substring(0, 200).TrimEnd();
        }

        return s.Length == 0 ? null : s;
    }
}

/// <summary>One keep in flight at a time and at most one per user per 10 seconds.</summary>
public class KeepGuard
{
    public static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(10);
    private readonly Func<DateTimeOffset> _now;
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _last = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public KeepGuard() : this(() => DateTimeOffset.UtcNow)
    {
    }

    public KeepGuard(Func<DateTimeOffset> now) => _now = now;

    /// <summary>True when the caller may go on (and must call <see cref="Exit"/>). Otherwise <paramref name="retryAfter"/> is the seconds to wait.</summary>
    public bool TryEnter(Guid user, out int retryAfter)
    {
        var now = _now();
        foreach (var kv in _last)
        {
            if (now - kv.Value >= Cooldown)
            {
                _last.TryRemove(kv.Key, out _);
            }
        }

        if (_last.TryGetValue(user, out var at) && now - at < Cooldown)
        {
            retryAfter = Math.Max(1, (int)Math.Ceiling((Cooldown - (now - at)).TotalSeconds));
            return false;
        }

        if (!_gate.Wait(0))
        {
            retryAfter = 2;
            return false;
        }

        _last[user] = now;
        retryAfter = 0;
        return true;
    }

    public void Exit() => _gate.Release();
}
