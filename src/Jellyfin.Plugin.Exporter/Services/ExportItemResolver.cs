using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Exporter.Export;
using Jellyfin.Plugin.Exporter.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.Exporter.Services;

/// <summary>
/// Turns a queued job into the file and metadata the encoder needs. Separated
/// from the engine so the engine can be tested with real FFmpeg and no server.
/// </summary>
public interface IExportItemResolver
{
    /// <summary>
    /// Resolves the Jellyfin item behind a job.
    /// </summary>
    /// <param name="job">Queued job.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Source path and layout metadata.</returns>
    /// <exception cref="ExportException">Thrown when the item or its file is gone.</exception>
    Task<ExportItemInfo> ResolveAsync(ExportJob job, CancellationToken cancellationToken);
}

/// <summary>
/// Resolves jobs through Jellyfin's library manager.
/// </summary>
public sealed class LibraryExportItemResolver : IExportItemResolver
{
    private readonly ILibraryManager _libraryManager;

    public LibraryExportItemResolver(ILibraryManager libraryManager)
    {
        _libraryManager = libraryManager;
    }

    /// <inheritdoc />
    public Task<ExportItemInfo> ResolveAsync(ExportJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        cancellationToken.ThrowIfCancellationRequested();

        var item = _libraryManager.GetItemById(job.JellyfinItemId)
            ?? throw new ExportException($"Jellyfin item {job.JellyfinItemId} no longer exists in the library.");

        var sourcePath = ResolveSourcePath(item);
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            throw new ExportException($"'{item.Name}' has no media file on disk (virtual item or missing media source).");
        }

        if (!File.Exists(sourcePath))
        {
            throw new ExportException($"The media file for '{item.Name}' is missing: {sourcePath}");
        }

        if (item is Episode episode)
        {
            return Task.FromResult(ExportItemInfo.ForEpisode(
                sourcePath,
                episode.SeriesName ?? "Unknown Show",
                episode.ParentIndexNumber,
                episode.IndexNumber,
                episode.Name));
        }

        return Task.FromResult(ExportItemInfo.ForMovie(sourcePath, item.Name, item.ProductionYear));
    }

    private static string? ResolveSourcePath(BaseItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.Path))
        {
            return item.Path;
        }

        if (item is IHasMediaSources hasMediaSources)
        {
            return hasMediaSources
                .GetMediaSources(false)
                .FirstOrDefault(source => !string.IsNullOrWhiteSpace(source.Path))
                ?.Path;
        }

        return null;
    }
}
