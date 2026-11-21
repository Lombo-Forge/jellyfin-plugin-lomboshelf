using System.Text.RegularExpressions;
using System.Text.Json;
using Jellyfin.Plugin.LomboShelf.Configuration;

namespace Jellyfin.Plugin.LomboShelf.Services;

/// <summary>Checks and cleans settings before they are saved. The colour rule equals SAFE in web/loader.js.</summary>
public static class ConfigSanitizer
{
    private static readonly Regex Safe = new(
        @"^(#[0-9a-fA-F]{3,8}|rgba?\([0-9., %]+\)|[0-9.]+(px|em|rem))$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private const int MaxList = 100;
    private const int MaxText = 100;

    /// <summary>Cleans <paramref name="c"/> in place. Returns the problems found; save only when the list is empty.</summary>
    public static List<string> Sanitize(PluginConfiguration c)
    {
        var errors = new List<string>();
        c.Bg = Color(c.Bg, "Background", errors);
        c.Surface = Color(c.Surface, "Surface", errors);
        c.Surface2 = Color(c.Surface2, "Surface 2", errors);
        c.Text = Color(c.Text, "Text", errors);
        c.Muted = Color(c.Muted, "Muted text", errors);
        c.Accent = Color(c.Accent, "Accent", errors);
        c.AccentHover = Color(c.AccentHover, "Accent hover", errors);
        c.Radius = Color(c.Radius, "Radius", errors);

        if (c.ShelfGenreLimit < 0)
        {
            errors.Add("Genre row limit cannot be negative.");
        }

        c.BrowseMergeLibraries = List(c.BrowseMergeLibraries, "Libraries to merge", errors);
        c.HeaderNavOrder = List(c.HeaderNavOrder, "Header menu order", errors);
        c.HeaderNavHide = List(c.HeaderNavHide, "Header menu hidden", errors);

        c.CollectionRows = Rows(c.CollectionRows, errors);

        var groups = HeaderNavGroups.TryParse(c.HeaderNavGroupsJson, out var groupError);
        if (groups is null)
        {
            errors.Add(groupError);
        }
        else
        {
            c.HeaderNavGroupsJson = JsonSerializer.Serialize(groups);
        }

        Integrations(c, errors);
        return errors;
    }

    public const int MaxToken = 256;
    private const int MaxPath = 500;

    /// <summary>The token to save: cleared on request, kept when the form sent none, else the new one. Returns it; never logs it.</summary>
    public static string MergeToken(PluginConfiguration incoming, string? stored)
    {
        if (incoming.ClearFarewellToken)
        {
            return string.Empty;
        }

        var sent = (incoming.FarewellToken ?? string.Empty).Trim();
        return sent.Length == 0 ? (stored ?? string.Empty) : sent;
    }

    /// <summary>A copy that is safe to send to a browser: the token is blank and FarewellTokenSet says whether one is stored.</summary>
    public static PluginConfiguration Redacted(PluginConfiguration c)
    {
        var copy = JsonSerializer.Deserialize<PluginConfiguration>(JsonSerializer.Serialize(c))!;
        copy.FarewellTokenSet = !string.IsNullOrEmpty(c.FarewellToken);
        copy.FarewellToken = string.Empty;
        copy.ClearFarewellToken = false;
        return copy;
    }

    private static void Integrations(PluginConfiguration c, List<string> errors)
    {
        c.TiersFile = (c.TiersFile ?? string.Empty).Trim();
        c.TiersPathPrefix = (c.TiersPathPrefix ?? string.Empty).Trim();
        c.LeavingFile = (c.LeavingFile ?? string.Empty).Trim();
        c.FarewellUrl = (c.FarewellUrl ?? string.Empty).Trim();
        c.FarewellToken = (c.FarewellToken ?? string.Empty).Trim();

        AbsolutePath(c.TiersFile, "Tiers file", errors);
        AbsolutePath(c.LeavingFile, "Leaving file", errors);

        if (c.TiersPathPrefix.Length > MaxPath || c.TiersPathPrefix.Any(char.IsControl)
            || c.TiersPathPrefix.Replace('\\', '/').Split('/').Contains(".."))
        {
            errors.Add("Tiers path prefix: at most 500 characters, no control characters and no \"..\".");
        }

        if (c.FarewellUrl.Length > 0)
        {
            var url = BaseUrl(c.FarewellUrl);
            if (url is null)
            {
                errors.Add("Farewell address: use http:// or https:// then the host and port only (no user name, path or query).");
            }
            else
            {
                c.FarewellUrl = url;
            }
        }

        if (c.FarewellToken.Length > MaxToken || c.FarewellToken.Any(ch => ch < '!' || ch > '~'))
        {
            errors.Add($"Farewell token: at most {MaxToken} characters, visible characters only (no spaces or control characters).");
        }

        if (c.StorageTagsEnabled && c.TiersFile.Length == 0)
        {
            errors.Add("Storage tags need the tiers file path.");
        }

        if (c.KeepEnabled)
        {
            if (c.LeavingFile.Length == 0) errors.Add("The keep button needs the leaving file path.");
            if (c.FarewellUrl.Length == 0) errors.Add("The keep button needs the Farewell address.");
            if (c.FarewellToken.Length == 0) errors.Add("The keep button needs the Farewell token.");
        }
    }

    private static void AbsolutePath(string value, string name, List<string> errors)
    {
        if (value.Length == 0)
        {
            return;
        }

        var rooted = value[0] == '/' || Regex.IsMatch(value, @"^[A-Za-z]:[\\/]", RegexOptions.NonBacktracking);
        if (!rooted || value.Length > MaxPath || value.Any(char.IsControl) || value.Replace('\\', '/').Split('/').Contains(".."))
        {
            errors.Add($"{name}: an absolute path (as Jellyfin sees it), at most 500 characters, no \"..\".");
        }
    }

    /// <summary>"scheme://host[:port]" for an http or https address with nothing else in it; null when it is anything else.</summary>
    public static string? BaseUrl(string value)
    {
        if (value.Length > MaxPath || value.Any(ch => char.IsControl(ch) || char.IsWhiteSpace(ch)) || value.IndexOfAny(new[] { '?', '#', '@', '\\' }) >= 0)
        {
            return null;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var u)
            || (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps)
            || u.UserInfo.Length > 0 || u.Host.Length == 0 || u.AbsolutePath != "/")
        {
            return null;
        }

        return u.GetLeftPart(UriPartial.Authority);
    }

    public const int MaxCollectionRows = 5;
    public const int MaxCollectionLimit = 50;
    public const int DefaultCollectionLimit = 16;

    /// <summary>Trims, drops rows without a name, turns limit 0 into the default; at most 5 rows, limit 1-50.</summary>
    private static CollectionRow[] Rows(CollectionRow[]? rows, List<string> errors)
    {
        var clean = new List<CollectionRow>();
        foreach (var r in (rows ?? Array.Empty<CollectionRow>()).Where(x => x is not null))
        {
            var name = (r.Name ?? string.Empty).Trim();
            if (name.Length == 0)
            {
                continue;
            }

            var title = (r.Title ?? string.Empty).Trim();
            if (name.Length > MaxText || title.Length > MaxText)
            {
                errors.Add($"Collection rows: names and titles can have at most {MaxText} characters.");
            }

            if (r.Limit < 0 || r.Limit > MaxCollectionLimit)
            {
                errors.Add($"Collection rows: the limit must be between 1 and {MaxCollectionLimit}.");
            }

            clean.Add(new CollectionRow { Name = name, Title = title, Limit = r.Limit == 0 ? DefaultCollectionLimit : r.Limit });
        }

        if (clean.Count > MaxCollectionRows)
        {
            errors.Add($"Collection rows: at most {MaxCollectionRows}.");
        }

        return clean.ToArray();
    }

    private static string Color(string? value, string name, List<string> errors)
    {
        var v = (value ?? string.Empty).Trim();
        if (!Safe.IsMatch(v))
        {
            errors.Add($"{name} must be a colour like #e50914 or rgb(229, 9, 20) (a size like 6px for the radius).");
        }

        return v;
    }

    private static string[] List(string[]? values, string name, List<string> errors)
    {
        var clean = (values ?? Array.Empty<string>()).Select(x => (x ?? string.Empty).Trim()).Where(x => x.Length > 0).ToArray();
        if (clean.Length > MaxList || clean.Any(x => x.Length > MaxText))
        {
            errors.Add($"{name}: at most {MaxList} entries of {MaxText} characters.");
        }

        return clean;
    }
}
