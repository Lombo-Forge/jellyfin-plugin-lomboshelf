using System.Xml.Serialization;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.LomboShelf.Configuration;

/// <summary>
/// Settings, saved as XML by Jellyfin. They mirror window.LomboShelfConfig in web/loader.js
/// (defaults must stay equal to the defaults there). See docs/design.md, Settings.
/// </summary>
/// <summary>One collection row: the collection's exact name, an optional row title, and how many items (1-50; 0 = default 16).</summary>
public class CollectionRow
{
    public string Name { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public int Limit { get; set; }
}

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

    /// <summary>Jellyfin collections shown as Home rows, by exact name. Empty (off) by default; at most 5 (see ConfigSanitizer).</summary>
    public CollectionRow[] CollectionRows { get; set; } = Array.Empty<CollectionRow>();

    // browse
    public string[] BrowseMergeLibraries { get; set; } = Array.Empty<string>();

    // nextSeason
    public bool NextSeasonShowArt { get; set; } = true;

    // headerNav (only sent when FeatureHeaderNav is on; groups are JSON text, see HeaderNavGroups)
    public string[] HeaderNavOrder { get; set; } = Array.Empty<string>();
    public string[] HeaderNavHide { get; set; } = Array.Empty<string>();

    /// <summary>Groups as JSON, e.g. [{"label":"Anime","items":[["Anime","Shows"]]}]. Checked on save; invalid text sends no groups.</summary>
    public string HeaderNavGroupsJson { get; set; } = "[]";

    // storage tags (off by default; reads Tier's tiers.json on the server, see docs/design.md)
    public bool StorageTagsEnabled { get; set; }
    public bool StorageTagsAdminsOnly { get; set; } = true;

    /// <summary>Absolute path of tiers.json as the Jellyfin process sees it.</summary>
    public string TiersFile { get; set; } = string.Empty;

    /// <summary>The part of a Jellyfin item path to strip so the rest equals the "path" in tiers.json, for example /media/.</summary>
    public string TiersPathPrefix { get; set; } = string.Empty;

    // keep button (off by default; reads Farewell's leaving.json and calls its keep route from the server)
    public bool KeepEnabled { get; set; }

    /// <summary>Absolute path of leaving.json as the Jellyfin process sees it.</summary>
    public string LeavingFile { get; set; } = string.Empty;

    /// <summary>http or https, host and port only.</summary>
    public string FarewellUrl { get; set; } = string.Empty;

    /// <summary>SECRET. Saved in the plugin's XML file, never sent to a browser: GET /LomboShelf/config returns it blank.</summary>
    public string FarewellToken { get; set; } = string.Empty;

    /// <summary>Only in answers to the settings page: true when a token is stored. Not saved.</summary>
    [XmlIgnore]
    public bool FarewellTokenSet { get; set; }

    /// <summary>Only in a save request: true clears the stored token (a blank token alone keeps it). Not saved.</summary>
    [XmlIgnore]
    public bool ClearFarewellToken { get; set; }
}
