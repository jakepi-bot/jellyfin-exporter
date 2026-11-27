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

        // Jellyfin's own transcoding settings (hardware switch, allowed
        // codecs, preset, CRFs, threads), read live for every job, save
        // validation and plan refusal.
        serviceCollection.AddSingleton<ITranscodingSettingsSource, JellyfinTranscodingSettingsSource>();

        // The queue is a singleton behind two contracts: IExportQueue for
        // callers that enqueue, and ExportQueue for the worker that drains it
        // and the status API that reports its persistence state.
        //
        // The store resolves the plugin data folder lazily: the container is
        // built before the plugin's data folder is guaranteed to exist.
        serviceCollection.AddSingleton<IExportJobStore>(
            _ => new JsonExportJobStore(() => Plugin.Instance?.DataFolderPath));
        serviceCollection.AddSingleton(sp => new ExportQueue(
            () => Plugin.Instance?.Configuration?.JobHistoryLimit ?? ExportQueue.DefaultRetentionLimit,
            sp.GetRequiredService<IExportJobStore>()));
        serviceCollection.AddSingleton<IExportQueue>(provider => provider.GetRequiredService<ExportQueue>());

        // Notifications: the listener is the only subscriber, so exactly one
        // message is delivered per finished job even if the queue raises a
        // duplicate event.
        serviceCollection.AddSingleton<IExportNotifier, ExportNotificationService>();
        serviceCollection.AddHostedService<ExportNotificationListener>();

        serviceCollection.AddSingleton<PathReservationRegistry>();
        serviceCollection.AddSingleton<IFfmpegLocator, MediaEncoderFfmpegLocator>();
        serviceCollection.AddSingleton<IExportItemResolver, LibraryExportItemResolver>();
        serviceCollection.AddSingleton<IExportSettings, PluginExportSettings>();
        serviceCollection.AddSingleton<IExportEngine, ExportEngine>();
        serviceCollection.AddHostedService<ExportQueueWorker>();
    }
}
