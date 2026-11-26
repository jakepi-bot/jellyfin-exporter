using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Exporter.Services;

/// <summary>
/// Minimal view of the Jellyfin media tree that export selection needs. The
/// server implementation reads it from <c>ILibraryManager</c>; tests supply a
/// fake so the expansion rules can be exercised without a server.
/// </summary>
public interface IMediaTreeSource
{
    /// <summary>
    /// Looks up one item.
    /// </summary>
    /// <param name="id">Jellyfin item id.</param>
    /// <returns>The item, or <c>null</c> when it no longer exists.</returns>
    MediaTreeNode? GetNode(Guid id);

    /// <summary>
    /// Gets the episodes below a series or season.
    /// </summary>
    /// <param name="parentId">Series or season id.</param>
    /// <param name="recursive">
    /// <c>true</c> for a series (all seasons); <c>false</c> for a single season.
    /// </param>
    /// <returns>Episodes in season/episode order.</returns>
    IReadOnlyList<MediaTreeNode> GetEpisodes(Guid parentId, bool recursive);
}
