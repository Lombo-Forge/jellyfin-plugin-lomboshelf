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
    Task<FarewellReply> KeepAsync(string baseUrl, string token, string itemIdHex, CancellationToken ct, string? by = null);
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

    public async Task<FarewellReply> KeepAsync(string baseUrl, string token, string itemIdHex, CancellationToken ct, string? by = null)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl.TrimEnd('/') + "/v1/keep");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            // Farewell 0.2.0 refuses any key but "title", so "by" is only added when the caller was given a name.
            var payload = new Dictionary<string, string> { ["title"] = itemIdHex };
            if (by is not null)
            {
                payload["by"] = by;
            }

            req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8);
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

/// <summary>The "by" name Farewell accepts: 1 to 60 printable characters with no space at either end.</summary>
public static class KeepName
{
    public const int Max = 60;

    /// <summary>
    /// A user name made to fit Farewell's rule: characters Farewell would call not printable (control, format, separator other than
    /// a plain space, unassigned, private use, lone surrogates) become spaces, the ends are trimmed, and it is cut to 60 characters
    /// (counted as code points, as Farewell does). Null when nothing is left.
    /// </summary>
    public static string? Clean(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        var sb = new StringBuilder();
        var count = 0;
        var pendingSpace = false;
        foreach (var rune in RunesOf(name))
        {
            if (count >= Max)
            {
                break;
            }

            var printable = rune is not null && (rune.Value.Value == ' ' || IsPrintable(rune.Value));
            if (!printable || rune!.Value.Value == ' ')
            {
                pendingSpace = sb.Length > 0;
                continue;
            }

            // A space only counts when something printable follows it, so the cut never leaves a trailing space.
            if (pendingSpace)
            {
                if (count + 1 >= Max)
                {
                    break;
                }

                sb.Append(' ');
                count++;
                pendingSpace = false;
            }

            sb.Append(rune.Value.ToString());
            count++;
        }

        return sb.Length == 0 ? null : sb.ToString();
    }

    private static IEnumerable<Rune?> RunesOf(string s)
    {
        for (var i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                yield return new Rune(s[i], s[i + 1]);
                i++;
            }
            else if (char.IsSurrogate(s[i]))
            {
                yield return null;   // a lone surrogate
            }
            else
            {
                yield return new Rune(s[i]);
            }
        }
    }

    private static bool IsPrintable(Rune r) => Rune.GetUnicodeCategory(r) is not (
        System.Globalization.UnicodeCategory.Control or System.Globalization.UnicodeCategory.Format
        or System.Globalization.UnicodeCategory.Surrogate or System.Globalization.UnicodeCategory.PrivateUse
        or System.Globalization.UnicodeCategory.OtherNotAssigned or System.Globalization.UnicodeCategory.LineSeparator
        or System.Globalization.UnicodeCategory.ParagraphSeparator or System.Globalization.UnicodeCategory.SpaceSeparator);
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
