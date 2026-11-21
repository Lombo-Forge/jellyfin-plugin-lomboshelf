namespace Jellyfin.Plugin.LomboShelf.Services;

using Jellyfin.Plugin.LomboShelf.Configuration;

/// <summary>One place that says whether an optional feature is really on: switched on and its required settings present.</summary>
public static class FeatureGate
{
    public static bool StorageTags(PluginConfiguration c) => c.StorageTagsEnabled && !string.IsNullOrWhiteSpace(c.TiersFile);

    /// <summary>The leaving note needs only the file, but the feature as a whole (note and button) needs the call settings too.</summary>
    public static bool Keep(PluginConfiguration c) =>
        c.KeepEnabled && !string.IsNullOrWhiteSpace(c.LeavingFile) && !string.IsNullOrWhiteSpace(c.FarewellUrl) && !string.IsNullOrWhiteSpace(c.FarewellToken);

    /// <summary>The travel card and its routes: switched on and Visa's address and token present.</summary>
    public static bool Travel(PluginConfiguration c) =>
        c.TravelEnabled && !string.IsNullOrWhiteSpace(c.VisaUrl) && !string.IsNullOrWhiteSpace(c.VisaToken);
}
