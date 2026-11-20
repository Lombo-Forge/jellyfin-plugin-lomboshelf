using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.LomboShelf.Services;

/// <summary>One drop-down group in the header menu: the shape web/header-nav.js reads (label plus [library, type] pairs).</summary>
public sealed record HeaderNavGroup(
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("items")] string[][] Items);

/// <summary>Parses and checks the header-menu groups text (the same JSON that goes to window.LomboShelfConfig.headerNav.groups).</summary>
public static class HeaderNavGroups
{
    public const int MaxGroups = 20;
    public const int MaxItems = 50;
    public const int MaxText = 100;

    /// <summary>Returns the groups, or null and an error message. Empty or blank text means no groups.</summary>
    public static IReadOnlyList<HeaderNavGroup>? TryParse(string? json, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<HeaderNavGroup>();
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() > MaxGroups)
            {
                error = $"Groups must be a list of at most {MaxGroups} groups.";
                return null;
            }

            var groups = new List<HeaderNavGroup>();
            foreach (var g in doc.RootElement.EnumerateArray())
            {
                if (g.ValueKind != JsonValueKind.Object
                    || !g.TryGetProperty("label", out var label) || !IsText(label)
                    || !g.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array
                    || items.GetArrayLength() > MaxItems)
                {
                    error = "Each group needs a \"label\" and an \"items\" list of [library, type] pairs.";
                    return null;
                }

                var pairs = new List<string[]>();
                foreach (var item in items.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Array || item.GetArrayLength() != 2
                        || !IsText(item[0]) || !IsText(item[1]))
                    {
                        error = "Each item must be a pair of two texts, for example [\"Anime\", \"Shows\"].";
                        return null;
                    }

                    pairs.Add(new[] { item[0].GetString()!, item[1].GetString()! });
                }

                groups.Add(new HeaderNavGroup(label.GetString()!, pairs.ToArray()));
            }

            return groups;
        }
        catch (JsonException)
        {
            error = "Groups is not valid JSON.";
            return null;
        }
    }

    /// <summary>The groups for the page, or an empty list when the text is not valid (fail open).</summary>
    public static IReadOnlyList<HeaderNavGroup> ParseOrEmpty(string? json) =>
        TryParse(json, out _) ?? Array.Empty<HeaderNavGroup>();

    private static bool IsText(JsonElement e) =>
        e.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(e.GetString()) && e.GetString()!.Length <= MaxText;
}
