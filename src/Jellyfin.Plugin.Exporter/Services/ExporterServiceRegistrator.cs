using Jellyfin.Plugin.Exporter.Configuration;
using Jellyfin.Plugin.Exporter.Export;
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

        // The queue is a singleton behind two contracts: IExportQueue for
        // callers that enqueue, and ExportQueue for the worker that drains it.
        serviceCollection.AddSingleton<ExportQueue>();
        serviceCollection.AddSingleton<IExportQueue>(provider => provider.GetRequiredService<ExportQueue>());

        serviceCollection.AddSingleton<PathReservationRegistry>();
        serviceCollection.AddSingleton<IFfmpegLocator, MediaEncoderFfmpegLocator>();
        serviceCollection.AddSingleton<IExportItemResolver, LibraryExportItemResolver>();
        serviceCollection.AddSingleton<IExportSettings, PluginExportSettings>();
        serviceCollection.AddSingleton<IExportEngine, ExportEngine>();
        serviceCollection.AddHostedService<ExportQueueWorker>();
    }
}
