using System.Text.Json;
using Jellyfin.Plugin.LomboShelf.Configuration;

namespace Jellyfin.Plugin.LomboShelf.Services;

/// <summary>Pure functions: settings to window.LomboShelfConfig JSON, and the index.html edit.</summary>
public static class IndexTransform
{
    public const string Marker = "<!-- lomboshelf -->";

    public static string BuildConfigJson(PluginConfiguration c)
    {
        var cfg = new Dictionary<string, object?>
        {
            ["theme"] = new Dictionary<string, object?>
            {
                ["bg"] = c.Bg, ["surface"] = c.Surface, ["surface2"] = c.Surface2, ["text"] = c.Text,
                ["muted"] = c.Muted, ["accent"] = c.Accent, ["accentHover"] = c.AccentHover, ["radius"] = c.Radius
            },
            ["features"] = new Dictionary<string, object?>
            {
                ["theme"] = c.FeatureTheme, ["homeShelf"] = c.FeatureHomeShelf, ["browse"] = c.FeatureBrowse,
                ["nextSeason"] = c.FeatureNextSeason, ["headerNav"] = c.FeatureHeaderNav
            },
            ["shelf"] = new Dictionary<string, object?>
            {
                ["resume"] = c.ShelfResume, ["keepWatching"] = c.ShelfKeepWatching, ["newThisWeek"] = c.ShelfNewThisWeek,
                ["genres"] = c.ShelfGenres, ["genreLimit"] = c.ShelfGenreLimit
            },
            // Same limits as the sanitiser, applied again here so a hand-edited XML file cannot send more.
            ["collectionRows"] = (c.CollectionRows ?? Array.Empty<CollectionRow>())
                .Where(r => r is not null && !string.IsNullOrWhiteSpace(r.Name))
                .Take(ConfigSanitizer.MaxCollectionRows)
                .Select(r =>
                {
                    var row = new Dictionary<string, object?>
                    {
                        ["name"] = r.Name.Trim(),
                        ["limit"] = r.Limit < 1 ? ConfigSanitizer.DefaultCollectionLimit : Math.Min(r.Limit, ConfigSanitizer.MaxCollectionLimit)
                    };
                    if (!string.IsNullOrWhiteSpace(r.Title))
                    {
                        row["title"] = r.Title.Trim();
                    }

                    return row;
                })
                .ToArray(),
            ["browse"] = new Dictionary<string, object?> { ["mergeLibraries"] = c.BrowseMergeLibraries },
            ["nextSeason"] = new Dictionary<string, object?> { ["showArt"] = c.NextSeasonShowArt }
        };
        if (c.FeatureHeaderNav)
        {
            cfg["headerNav"] = new Dictionary<string, object?>
            {
                ["order"] = c.HeaderNavOrder, ["groups"] = HeaderNavGroups.ParseOrEmpty(c.HeaderNavGroupsJson), ["hide"] = c.HeaderNavHide
            };
        }

        // The default encoder escapes <, > and & as \uXXXX, so the JSON cannot close the script tag.
        return JsonSerializer.Serialize(cfg);
    }

    /// <summary>Inserts the config script and loader script before the closing body tag. Idempotent.</summary>
    public static string Apply(string html, string configJson, string basePath = "/LomboShelf/web/", string? version = null)
    {
        if (html.Contains(Marker, StringComparison.Ordinal))
        {
            return html;
        }

        // The version in the URL makes browsers fetch fresh scripts after a plugin update (the route is cached for an hour).
        // Only plain version characters are kept, so it cannot break out of the attribute.
        var safe = version == null ? string.Empty : new string(version.Where(ch => char.IsAsciiLetterOrDigit(ch) || ch == '.' || ch == '-' || ch == '_').ToArray());
        var query = safe.Length > 0 ? "?v=" + safe : string.Empty;
        var block = Marker
            + "<script>window.LomboShelfConfig=" + configJson + ";</script>"
            + "<script src=\"" + basePath + "loader.js" + query + "\"></script>";
        var i = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        return i < 0 ? html + block : html.Insert(i, block);
    }
}
