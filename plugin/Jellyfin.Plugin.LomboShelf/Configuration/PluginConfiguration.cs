using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.LomboShelf.Configuration;

/// <summary>
/// Settings, saved as XML by Jellyfin. They mirror window.LomboShelfConfig in web/loader.js
/// (defaults must stay equal to the defaults there). See docs/design.md, Settings.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    // theme
    public string Bg { get; set; } = "#141414";
    public string Surface { get; set; } = "#1f1f1f";
    public string Surface2 { get; set; } = "#2a2a2a";
    public string Text { get; set; } = "#e5e5e5";
    public string Muted { get; set; } = "#a3a3a3";
    public string Accent { get; set; } = "#e50914";
    public string AccentHover { get; set; } = "#f6121d";
    public string Radius { get; set; } = "6px";

    // features
    public bool FeatureTheme { get; set; } = true;
    public bool FeatureHomeShelf { get; set; } = true;
    public bool FeatureBrowse { get; set; } = true;
    public bool FeatureNextSeason { get; set; }
    public bool FeatureHeaderNav { get; set; }

    // shelf
    public bool ShelfResume { get; set; } = true;
    public bool ShelfKeepWatching { get; set; } = true;
    public bool ShelfNewThisWeek { get; set; } = true;
    public bool ShelfGenres { get; set; } = true;
    public int ShelfGenreLimit { get; set; }

    // browse
    public string[] BrowseMergeLibraries { get; set; } = Array.Empty<string>();

    // nextSeason
    public bool NextSeasonShowArt { get; set; } = true;

    // headerNav (only sent when FeatureHeaderNav is on; groups are JSON text, see HeaderNavGroups)
    public string[] HeaderNavOrder { get; set; } = Array.Empty<string>();
    public string[] HeaderNavHide { get; set; } = Array.Empty<string>();

    /// <summary>Groups as JSON, e.g. [{"label":"Anime","items":[["Anime","Shows"]]}]. Checked on save; invalid text sends no groups.</summary>
    public string HeaderNavGroupsJson { get; set; } = "[]";
}
