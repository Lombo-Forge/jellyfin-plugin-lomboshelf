using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LomboShelf.Services;

/// <summary>What Visa says about one person: the end of an open window (unix seconds), when a new one is allowed, and the longest window.</summary>
public sealed record TravelState(long? Ends, long? NextAllowed, int MaxDays);

/// <summary>Visa's answer: HTTP status (0 when no usable answer came), its message (cleaned) and, for a status call, the state.</summary>
public sealed record VisaReply(int Status, string? Message, TravelState? State = null);

public interface IVisaClient
{
    Task<VisaReply> StatusAsync(string baseUrl, string token, string userIdHex, CancellationToken ct);

    Task<VisaReply> OpenAsync(string baseUrl, string token, string userIdHex, int days, CancellationToken ct);
}

/// <summary>Calls Visa's travel routes. The token is only ever put in the Authorization header of that one request.</summary>
public sealed class VisaClient : IVisaClient
{
    public const int MaxDaysSeen = 3650;
    private readonly HttpClient _http;
    private readonly ILogger<VisaClient>? _log;

    public VisaClient(ILogger<VisaClient>? log = null)
        : this(new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(10) }, log)
    {
    }

    /// <summary>For tests: the caller supplies the handler (no network).</summary>
    public VisaClient(HttpClient http, ILogger<VisaClient>? log = null)
    {
        _http = http;
        _http.Timeout = TimeSpan.FromSeconds(10);
        _log = log;
    }

    public async Task<VisaReply> StatusAsync(string baseUrl, string token, string userIdHex, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, baseUrl.TrimEnd('/') + "/v1/travel/" + userIdHex);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            var status = (int)resp.StatusCode;
            if (status != 200)
            {
                return new VisaReply(status, null);
            }

            var body = await FarewellClient.ReadLimitedAsync(resp, ct).ConfigureAwait(false);
            var state = body is null ? null : StateFrom(body);
            return state is null ? new VisaReply(0, null) : new VisaReply(200, null, state);
        }
        catch (Exception ex)
        {
            // Only the exception type: its message can hold the address.
            _log?.LogWarning("The travel status request to Visa did not complete ({Type}).", ex.GetType().Name);
            return new VisaReply(0, null);
        }
    }

    public async Task<VisaReply> OpenAsync(string baseUrl, string token, string userIdHex, int days, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl.TrimEnd('/') + "/v1/travel");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Content = new StringContent(JsonSerializer.Serialize(new Dictionary<string, object> { ["user_id"] = userIdHex, ["days"] = days }), Encoding.UTF8);
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            var status = (int)resp.StatusCode;
            if (status != 200 && status != 422)
            {
                return new VisaReply(status, null);
            }

            var body = await FarewellClient.ReadLimitedAsync(resp, ct).ConfigureAwait(false);
            return body is null ? new VisaReply(0, null) : new VisaReply(status, FarewellClient.MessageFrom(body));
        }
        catch (Exception ex)
        {
            _log?.LogWarning("The travel request to Visa did not complete ({Type}).", ex.GetType().Name);
            return new VisaReply(0, null);
        }
    }

    /// <summary>Reads {"active": {"ends": n}|null, "next_allowed": n|null, "max_days": n}; null when the shape is not exactly that.</summary>
    internal static TravelState? StateFrom(ReadOnlySpan<byte> body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body.ToArray());
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("active", out var active) || !root.TryGetProperty("next_allowed", out var next)
                || !root.TryGetProperty("max_days", out var max) || max.ValueKind != JsonValueKind.Number
                || !max.TryGetInt32(out var maxDays) || maxDays < 1 || maxDays > MaxDaysSeen)
            {
                return null;
            }

            long? ends = null;
            if (active.ValueKind == JsonValueKind.Object)
            {
                if (!active.TryGetProperty("ends", out var e) || !Seconds(e, out var v))
                {
                    return null;
                }

                ends = v;
            }
            else if (active.ValueKind != JsonValueKind.Null)
            {
                return null;
            }

            long? nextAllowed = null;
            if (next.ValueKind != JsonValueKind.Null)
            {
                if (!Seconds(next, out var v))
                {
                    return null;
                }

                nextAllowed = v;
            }

            return new TravelState(ends, nextAllowed, maxDays);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool Seconds(JsonElement e, out long value)
    {
        value = 0;
        return e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out value) && value >= 0 && value < 32503680000L;
    }
}

/// <summary>One travel request in flight at a time and at most one per user per 10 seconds (its own counters, apart from keep).</summary>
public sealed class TravelGuard : KeepGuard
{
    public TravelGuard()
    {
    }

    public TravelGuard(Func<DateTimeOffset> now) : base(now)
    {
    }
}
