using Jellyfin.Plugin.LomboShelf.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.LomboShelf;

/// <summary>The LomboShelf plugin: settings page plus the web files injected into jellyfin-web.</summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>Fixed plugin id. Never change it after a release.</summary>
    public static readonly Guid PluginId = Guid.Parse("6f1c2a54-3b7d-4e0a-9c85-2d41a7e0b913");

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "LomboShelf";

    public override Guid Id => PluginId;

    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = Name,
            EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html"
        };
    }
}
