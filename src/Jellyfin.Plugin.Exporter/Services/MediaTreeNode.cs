using System;

namespace Jellyfin.Plugin.Exporter.Services;

/// <summary>
/// One media item as the export selection sees it: enough identity to build a
/// job and a readable label, without pulling Jellyfin entity types into the
/// planning code that is unit tested.
/// </summary>
/// <param name="Id">Jellyfin item id.</param>
/// <param name="Name">Item name as Jellyfin reports it.</param>
/// <param name="ItemType">Jellyfin item kind name, e.g. <c>Movie</c>, <c>Episode</c>, <c>Series</c>, <c>Season</c>.</param>
/// <param name="SeriesName">Series a season or episode belongs to, when known.</param>
/// <param name="SeasonNumber">Season number for episodes.</param>
/// <param name="EpisodeNumber">Episode number for episodes.</param>
public sealed record MediaTreeNode(
    Guid Id,
    string Name,
    string ItemType,
    string? SeriesName = null,
    int? SeasonNumber = null,
    int? EpisodeNumber = null);
