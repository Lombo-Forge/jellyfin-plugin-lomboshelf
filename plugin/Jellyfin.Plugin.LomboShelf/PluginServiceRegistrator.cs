using Jellyfin.Plugin.LomboShelf.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.LomboShelf;

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<IFileTransformationRegistrar, FileTransformationRegistrar>();
        serviceCollection.AddHostedService<StartupRegistration>();
    }
}
