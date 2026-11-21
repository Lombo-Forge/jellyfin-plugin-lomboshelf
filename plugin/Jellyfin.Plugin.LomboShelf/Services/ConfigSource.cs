using Jellyfin.Plugin.LomboShelf.Configuration;

namespace Jellyfin.Plugin.LomboShelf.Services;

/// <summary>The saved settings, behind an interface so the routes can be tested.</summary>
public interface IConfigSource
{
    PluginConfiguration Current { get; }
}

public sealed class PluginConfigSource : IConfigSource
{
    public PluginConfiguration Current => Plugin.Instance?.Configuration ?? new PluginConfiguration();
}
