using System.Collections.Concurrent;
using System.Text.Json;

namespace Jellyfin.Plugin.LomboShelf.Services;

/// <summary>One entry of Farewell's leaving.json.</summary>
public sealed record LeavingEntry(int? DaysLeft, bool CanKeep);

/// <summary>
/// Reads the two JSON files other apps write (Tier's tiers.json, Farewell's leaving.json). Opens only the configured path,
/// refuses files over 4 MB and any "version" other than 1, caches by last-write time and size, and never throws:
/// every problem means "no data" (null).
/// </summary>
public sealed class ContractReader
{
    public const long MaxBytes = 4 * 1024 * 1024;

    private sealed record Cached(DateTime Written, long Length, object? Value);

    private readonly ConcurrentDictionary<string, Cached> _cache = new(StringComparer.Ordinal);

    /// <summary>path (as in tiers.json, normalised) to "ssd" or "hdd".</summary>
    public IReadOnlyDictionary<string, string>? ReadTiers(string? file) => Read(file, "t", ParseTiers) as IReadOnlyDictionary<string, string>;

    /// <summary>id (32 lower-case hex) to its entry.</summary>
    public IReadOnlyDictionary<string, LeavingEntry>? ReadLeaving(string? file) => Read(file, "l", ParseLeaving) as IReadOnlyDictionary<string, LeavingEntry>;

    private object? Read(string? file, string kind, Func<JsonElement, object?> parse)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(file))
            {
                return null;
            }

            var key = kind + "|" + file;
            var info = new FileInfo(file);
            if (!info.Exists || info.Length > MaxBytes)
            {
                _cache.TryRemove(key, out _);
                return null;
            }

            var written = info.LastWriteTimeUtc;
            if (_cache.TryGetValue(key, out var hit) && hit.Written == written && hit.Length == info.Length)
            {
                return hit.Value;
            }

            byte[] bytes;
            using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                // Read at most one byte more than the limit, so a file that grew after the size check is still refused.
                using var ms = new MemoryStream();
                var buffer = new byte[16 * 1024];
                int n;
                while ((n = fs.Read(buffer, 0, buffer.Length)) > 0)
                {
                    ms.Write(buffer, 0, n);
                    if (ms.Length > MaxBytes)
                    {
                        return null;
                    }
                }

                bytes = ms.ToArray();
            }

            object? value = null;
            using (var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 }))
            {
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object
                    && root.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var ver) && ver == 1)
                {
                    value = parse(root);
                }
            }

            _cache[key] = new Cached(written, info.Length, value);
            return value;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static object? ParseTiers(JsonElement root)
    {
        if (!root.TryGetProperty("units", out var units) || units.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var u in units.EnumerateArray())
        {
            if (u.ValueKind != JsonValueKind.Object
                || !u.TryGetProperty("path", out var p) || p.ValueKind != JsonValueKind.String
                || !u.TryGetProperty("tier", out var t) || t.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var tier = t.GetString();
            var path = StorageResolver.Normalise(p.GetString());
            if ((tier == "ssd" || tier == "hdd") && !string.IsNullOrEmpty(path))
            {
                map[path] = tier;
            }
        }

        return map;
    }

    private static object? ParseLeaving(JsonElement root)
    {
        if (!root.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var map = new Dictionary<string, LeavingEntry>(StringComparer.Ordinal);
        foreach (var e in entries.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var key = ItemIds.Normalise(id.GetString());
            if (key is null)
            {
                continue;
            }

            int? days = null;
            if (e.TryGetProperty("days_left", out var d) && d.ValueKind == JsonValueKind.Number && d.TryGetInt32(out var n) && n >= 0)
            {
                days = n;
            }

            var canKeep = e.TryGetProperty("can_keep", out var k) && k.ValueKind == JsonValueKind.True;
            map[key] = new LeavingEntry(days, canKeep);
        }

        return map;
    }
}

public static class ItemIds
{
    /// <summary>32 lower-case hex characters from Jellyfin's plain or dashed form; null for anything else.</summary>
    public static string? Normalise(string? id)
    {
        if (id is null || id.Length > 36)
        {
            return null;
        }

        if (id.Length == 32 && Guid.TryParseExact(id, "N", out var a))
        {
            return a.ToString("N");
        }

        if (id.Length == 36 && Guid.TryParseExact(id, "D", out var b))
        {
            return b.ToString("N");
        }

        return null;
    }
}

/// <summary>Finds the tiers.json unit for an item path. Pure.</summary>
public static class StorageResolver
{
    /// <summary>Forward slashes, no empty or "." parts, no leading slash. Null when the path holds "..".</summary>
    public static string? Normalise(string? path)
    {
        if (path is null)
        {
            return null;
        }

        var parts = new List<string>();
        foreach (var part in path.Replace('\\', '/').Split('/'))
        {
            if (part.Length == 0 || part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                return null;
            }

            parts.Add(part);
        }

        return string.Join('/', parts);
    }

    /// <summary>
    /// Strips <paramref name="prefix"/> from the item's file or folder path and returns the tier of the longest unit
    /// folder that equals it or contains it. Null when the path does not start with the prefix, holds "..", or no unit matches.
    /// </summary>
    public static string? Resolve(string? itemPath, string? prefix, IReadOnlyDictionary<string, string> units)
    {
        var item = Normalise(itemPath);
        var pre = Normalise(prefix ?? string.Empty);
        if (string.IsNullOrEmpty(item) || pre is null)
        {
            return null;
        }

        if (pre.Length > 0)
        {
            if (!item.StartsWith(pre + "/", StringComparison.Ordinal))
            {
                return null;
            }

            item = item.Substring(pre.Length + 1);
        }

        for (var p = item; p.Length > 0;)
        {
            if (units.TryGetValue(p, out var tier))
            {
                return tier;
            }

            var cut = p.LastIndexOf('/');
            p = cut < 0 ? string.Empty : p.Substring(0, cut);
        }

        return null;
    }
}
