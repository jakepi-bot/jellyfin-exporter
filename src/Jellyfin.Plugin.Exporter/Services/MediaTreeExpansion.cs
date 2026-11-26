using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.Exporter.Services;

/// <summary>
/// Something the operator selected that cannot become export jobs.
/// </summary>
/// <param name="ItemId">Selected item id, when it still exists.</param>
/// <param name="Message">Operator-facing explanation.</param>
public sealed record SelectionProblem(Guid? ItemId, string Message);

/// <summary>
/// Result of turning a selection of library items into the leaf items the
/// exporter can actually encode.
/// </summary>
/// <param name="Items">Leaf items (movies, episodes, videos) in selection order, de-duplicated.</param>
/// <param name="MissingItemIds">Selected ids Jellyfin no longer knows about.</param>
/// <param name="Problems">Selections that were understood but cannot be exported.</param>
public sealed record MediaExpansion(
    IReadOnlyList<MediaTreeNode> Items,
    IReadOnlyList<Guid> MissingItemIds,
    IReadOnlyList<SelectionProblem> Problems)
{
    /// <summary>
    /// Gets an expansion with nothing in it.
    /// </summary>
    public static MediaExpansion Empty { get; } = new(
        Array.Empty<MediaTreeNode>(),
        Array.Empty<Guid>(),
        Array.Empty<SelectionProblem>());
}

/// <summary>
/// Expands a mixed selection of movies, shows and seasons into the individual
/// items that get one export job each.
/// </summary>
public static class MediaTreeExpansion
{
    /// <summary>Item kinds that are exported as they are.</summary>
    public static readonly IReadOnlyList<string> DirectExportItemTypes = new[]
    {
        "Movie", "Episode", "Video", "MusicVideo", "Trailer"
    };

    /// <summary>Item kinds that expand into their episodes.</summary>
    public static readonly IReadOnlyList<string> EpisodeContainerItemTypes = new[]
    {
        "Series", "Season"
    };

    /// <summary>
    /// Expands one selection.
    /// </summary>
    /// <param name="source">Media tree to read from.</param>
    /// <param name="selection">Ids the operator picked, in the order they were picked.</param>
    /// <returns>The leaf items and anything that could not be expanded.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="source"/> is null.</exception>
    public static MediaExpansion Expand(IMediaTreeSource? source, IEnumerable<Guid>? selection)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (selection is null)
        {
            return MediaExpansion.Empty;
        }

        var items = new List<MediaTreeNode>();
        var seen = new HashSet<Guid>();
        var missing = new List<Guid>();
        var problems = new List<SelectionProblem>();

        void Add(MediaTreeNode node)
        {
            if (seen.Add(node.Id))
            {
                items.Add(node);
            }
        }

        void AddEpisodes(MediaTreeNode container, bool recursive)
        {
            var episodes = source.GetEpisodes(container.Id, recursive);
            if (episodes is null || episodes.Count == 0)
            {
                problems.Add(new SelectionProblem(
                    container.Id,
                    $"'{container.Name}' has no episodes with files to export."));
                return;
            }

            foreach (var episode in episodes)
            {
                Add(episode);
            }
        }

        foreach (var id in selection.Where(id => id != Guid.Empty).Distinct())
        {
            var node = source.GetNode(id);
            if (node is null)
            {
                missing.Add(id);
                continue;
            }

            if (DirectExportItemTypes.Contains(node.ItemType, StringComparer.OrdinalIgnoreCase))
            {
                Add(node);
                continue;
            }

            if (string.Equals(node.ItemType, "Series", StringComparison.OrdinalIgnoreCase))
            {
                AddEpisodes(node, recursive: true);
                continue;
            }

            if (string.Equals(node.ItemType, "Season", StringComparison.OrdinalIgnoreCase))
            {
                AddEpisodes(node, recursive: false);
                continue;
            }

            problems.Add(new SelectionProblem(
                node.Id,
                $"'{node.Name}' is a {node.ItemType}. Exports support movies, episodes, shows and seasons."));
        }

        return new MediaExpansion(items, missing, problems);
    }
}
