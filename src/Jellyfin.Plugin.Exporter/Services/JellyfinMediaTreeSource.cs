using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Exporter.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.Exporter.Services;

/// <summary>
/// Reads the Jellyfin library through <see cref="ILibraryManager"/> and maps it
/// onto <see cref="MediaTreeNode"/>s, so the export planning code never has to
/// touch server entity types.
/// </summary>
public sealed class JellyfinMediaTreeSource : IMediaTreeSource
{
    private readonly ILibraryManager _libraryManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="JellyfinMediaTreeSource"/> class.
    /// </summary>
    /// <param name="libraryManager">Server library.</param>
    public JellyfinMediaTreeSource(ILibraryManager libraryManager)
    {
        _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
    }

    /// <inheritdoc />
    public MediaTreeNode? GetNode(Guid id)
    {
        if (id == Guid.Empty)
        {
            return null;
        }

        var item = _libraryManager.GetItemById(id);
        return item is null ? null : ToNode(item);
    }

    /// <inheritdoc />
    public IReadOnlyList<MediaTreeNode> GetEpisodes(Guid parentId, bool recursive)
    {
        var query = new InternalItemsQuery
        {
            ParentId = parentId,
            Recursive = recursive,
            IncludeItemTypes = new[] { BaseItemKind.Episode },
            // Only "has a file" matters here. IsUnaired must stay unset: on
            // Jellyfin it excludes every episode without a premiere date (a
            // library with no remote metadata providers has none), which made
            // whole seasons report "no episodes with files to export".
            IsMissing = false
        };

        return _libraryManager
            .GetItemList(query)
            .Select(ToNode)
            .OrderBy(node => node.SeasonNumber ?? int.MaxValue)
            .ThenBy(node => node.EpisodeNumber ?? int.MaxValue)
            .ToList();
    }

    /// <summary>
    /// Maps a server entity onto the tree node used by the selection code.
    /// </summary>
    /// <param name="item">Server entity.</param>
    /// <returns>Tree node.</returns>
    public static MediaTreeNode ToNode(BaseItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        var kind = item.GetBaseItemKind().ToString();
        switch (item)
        {
            case Episode episode:
                return new MediaTreeNode(
                    episode.Id,
                    episode.Name,
                    kind,
                    string.IsNullOrWhiteSpace(episode.SeriesName) ? null : episode.SeriesName,
                    episode.ParentIndexNumber,
                    episode.IndexNumber);

            case Season season:
                return new MediaTreeNode(
                    season.Id,
                    season.Name,
                    kind,
                    string.IsNullOrWhiteSpace(season.SeriesName) ? null : season.SeriesName,
                    season.IndexNumber,
                    null);

            default:
                return new MediaTreeNode(item.Id, item.Name, kind);
        }
    }

    /// <summary>
    /// Builds the poster metadata the browse page needs for one item. Seasons
    /// often carry no artwork of their own, so their series artwork is used.
    /// </summary>
    /// <param name="item">Server entity.</param>
    /// <returns>Image item id and cache tag, or nulls when there is no artwork.</returns>
    public static (Guid ImageItemId, string? ImageTag) GetPoster(BaseItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        var image = item.ImageInfos?.FirstOrDefault(i => i.Type == MediaBrowser.Model.Entities.ImageType.Primary);
        if (image is not null)
        {
            return (item.Id, image.DateModified.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (item is Season season && season.Series is Series series)
        {
            var seriesImage = series.ImageInfos?.FirstOrDefault(i => i.Type == MediaBrowser.Model.Entities.ImageType.Primary);
            if (seriesImage is not null)
            {
                return (series.Id, seriesImage.DateModified.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        return (item.Id, null);
    }
}
