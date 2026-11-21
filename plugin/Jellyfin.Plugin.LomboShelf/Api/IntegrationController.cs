using System.Text.Json;
using Jellyfin.Plugin.LomboShelf.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LomboShelf.Api;

/// <summary>
/// The two optional features that read other apps' files: storage tags (Tier's tiers.json) and the keep button
/// (Farewell's leaving.json and keep call). Any signed-in user; each route answers 404 when its feature is off.
/// The browser never sees a file path or the Farewell token: the server resolves paths and holds the token.
/// Answers are JSON with exactly the keys documented in docs/design.md and are never cached.
/// </summary>
[ApiController]
[Authorize(Policy = "DefaultAuthorization")]
[Route("LomboShelf")]
public class IntegrationController : ControllerBase
{
    private const int MaxBody = 1024;
    private readonly IConfigSource _config;
    private readonly IItemAccess _items;
    private readonly ContractReader _files;
    private readonly IFarewellClient _farewell;
    private readonly KeepGuard _guard;
    private readonly ILogger<IntegrationController> _log;

    public IntegrationController(IConfigSource config, IItemAccess items, ContractReader files, IFarewellClient farewell, KeepGuard guard, ILogger<IntegrationController> log)
    {
        _config = config;
        _items = items;
        _files = files;
        _farewell = farewell;
        _guard = guard;
        _log = log;
    }

    [HttpGet("storage/{itemId}")]
    public IActionResult Storage(string itemId)
    {
        NoStore();
        var c = _config.Current;
        if (!FeatureGate.StorageTags(c))
        {
            return NotFound();
        }

        var caller = CallerId();
        if (caller is null)
        {
            return Unauthorized();
        }

        var id = ParseGuid(itemId);
        if (id is null)
        {
            return NoContent();
        }

        var facts = _items.Find(id.Value, caller.Value);
        if (facts is null || facts.Kind == ItemKind.Other || (c.StorageTagsAdminsOnly && !facts.CallerIsAdmin))
        {
            return NoContent();
        }

        var tiers = _files.ReadTiers(c.TiersFile);
        var tier = tiers is null ? null : StorageResolver.Resolve(facts.Path, c.TiersPathPrefix, tiers);
        return tier is null ? NoContent() : Json(200, new Dictionary<string, object?> { ["tier"] = tier });
    }

    [HttpGet("leaving/{itemId}")]
    public IActionResult Leaving(string itemId)
    {
        NoStore();
        var c = _config.Current;
        if (!FeatureGate.Keep(c))
        {
            return NotFound();
        }

        var caller = CallerId();
        if (caller is null)
        {
            return Unauthorized();
        }

        var key = ItemIds.Normalise(itemId);
        var id = ParseGuid(itemId);
        if (key is null || id is null || _items.Find(id.Value, caller.Value) is null)
        {
            return NoContent();
        }

        var leaving = _files.ReadLeaving(c.LeavingFile);
        if (leaving is null || !leaving.TryGetValue(key, out var entry))
        {
            return NoContent();
        }

        return Json(200, new Dictionary<string, object?> { ["days_left"] = entry.DaysLeft, ["can_keep"] = entry.CanKeep });
    }

    [HttpPost("keep")]
    public async Task<IActionResult> Keep(CancellationToken ct)
    {
        NoStore();
        var c = _config.Current;
        if (!FeatureGate.Keep(c))
        {
            return NotFound();
        }

        var caller = CallerId();
        if (caller is null)
        {
            return Unauthorized();
        }

        var key = ItemIds.Normalise(await ReadItemIdAsync(ct).ConfigureAwait(false));
        var id = key is null ? null : ParseGuid(key);
        if (key is null || id is null)
        {
            return Fail(400, "bad request");
        }

        // Unknown, hidden, not leaving and not keepable all get the same answer, so a user learns nothing about titles they cannot see.
        var leaving = _files.ReadLeaving(c.LeavingFile);
        if (_items.Find(id.Value, caller.Value) is null || leaving is null || !leaving.TryGetValue(key, out var entry) || !entry.CanKeep)
        {
            return Fail(422, "this title cannot be kept right now");
        }

        if (!_guard.TryEnter(caller.Value, out var retry))
        {
            Response.Headers.RetryAfter = retry.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return Fail(429, "please wait a few seconds and try again");
        }

        FarewellReply reply;
        try
        {
            reply = await _farewell.KeepAsync(c.FarewellUrl, c.FarewellToken, key, ct).ConfigureAwait(false);
        }
        finally
        {
            _guard.Exit();
        }

        switch (reply.Status)
        {
            case 200:
                return Json(200, new Dictionary<string, object?> { ["ok"] = true, ["message"] = reply.Message ?? "done" });
            case 422:
                return Fail(422, reply.Message ?? "this title cannot be kept right now");
            default:
                _log.LogWarning("A keep request did not complete.");
                return Fail(502, "could not complete the request");
        }
    }

    private async Task<string?> ReadItemIdAsync(CancellationToken ct)
    {
        try
        {
            var buffer = new byte[MaxBody + 1];
            var total = 0;
            int n;
            while (total < buffer.Length && (n = await Request.Body.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct).ConfigureAwait(false)) > 0)
            {
                total += n;
            }

            if (total > MaxBody)
            {
                return null;
            }

            using var doc = JsonDocument.Parse(buffer.AsMemory(0, total));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            string? id = null;
            var count = 0;
            foreach (var p in root.EnumerateObject())
            {
                count++;
                id = p.Name == "itemId" && p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : null;
            }

            return count == 1 ? id : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    private Guid? CallerId()
    {
        var v = User.FindFirst("Jellyfin-UserId")?.Value;
        return Guid.TryParse(v, out var g) && g != Guid.Empty ? g : null;
    }

    private static Guid? ParseGuid(string? s)
    {
        var n = ItemIds.Normalise(s);
        return n is not null && Guid.TryParseExact(n, "N", out var g) ? g : null;
    }

    private void NoStore() => Response.Headers.CacheControl = "no-store";

    private IActionResult Fail(int status, string message) =>
        Json(status, new Dictionary<string, object?> { ["ok"] = false, ["message"] = message });

    // Built by hand: Jellyfin's MVC serializer would turn the keys into PascalCase.
    private static ContentResult Json(int status, object body) =>
        new() { StatusCode = status, ContentType = "application/json", Content = JsonSerializer.Serialize(body) };
}
