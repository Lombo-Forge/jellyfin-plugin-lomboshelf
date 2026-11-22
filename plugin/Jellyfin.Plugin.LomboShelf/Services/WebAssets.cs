namespace Jellyfin.Plugin.LomboShelf.Services;

/// <summary>The only files the plugin serves from its embedded web folder.</summary>
public static class WebAssets
{
    public static readonly IReadOnlyDictionary<string, string> Allowed = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["loader.js"] = "text/javascript",
        ["ui.js"] = "text/javascript",
        ["home-shelf.js"] = "text/javascript",
        ["browse-genres.js"] = "text/javascript",
        ["next-season.js"] = "text/javascript",
        ["header-nav.js"] = "text/javascript",
        ["storage-tag.js"] = "text/javascript",
        ["keep-button.js"] = "text/javascript",
        ["travel-button.js"] = "text/javascript",
        ["lomboshelf.css"] = "text/css"
    };

    /// <summary>Exact, case-sensitive match against the list; anything else (paths, dots, slashes, empty) is refused.</summary>
    public static bool TryGetContentType(string? file, out string contentType)
    {
        contentType = string.Empty;
        return file is not null && Allowed.TryGetValue(file, out contentType!);
    }
}
