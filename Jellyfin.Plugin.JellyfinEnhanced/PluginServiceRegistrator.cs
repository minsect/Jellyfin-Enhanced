using Jellyfin.Plugin.JellyfinEnhanced.Services;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using MediaBrowser.Controller;

namespace Jellyfin.Plugin.JellyfinEnhanced
{
    public class PluginServiceRegistrator : IPluginServiceRegistrator
    {
        public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
        {
            serviceCollection.AddHostedService<SLSKDService>();
            serviceCollection.AddSingleton<StartupService>();
            serviceCollection.AddHttpClient();
            serviceCollection.AddSingleton<Logger>();
            serviceCollection.AddSingleton<SLSKDStore>();
        }
    }
}