using Jellyfin.Plugin.Exporter.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Exporter.Services;

/// <summary>
/// Registers the plugin's own services with the server container.
/// </summary>
/// <remarks>
/// This must be a separate class with a parameterless constructor. The server
/// builds the service registrator with <see cref="System.Activator"/>, and a
/// plugin that implements <see cref="IPluginServiceRegistrator"/> directly fails
/// that instantiation and is disabled at load time.
/// </remarks>
public class ExporterServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<IOutputDirectoryInspector, OutputDirectoryInspector>();
    }
}
