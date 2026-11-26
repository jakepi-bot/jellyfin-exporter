using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Exporter.Services;
using Xunit;

namespace Jellyfin.Plugin.Exporter.Tests;

public class MediaTreeExpansionTests
{
    /// <summary>In-memory media tree: nodes plus their direct children.</summary>
    private sealed class FakeTree : IMediaTreeSource
    {
        private readonly Dictionary<Guid, MediaTreeNode> _nodes = new();
        private readonly Dictionary<Guid, List<MediaTreeNode>> _children = new();

        public FakeTree Add(MediaTreeNode node)
        {
            _nodes[node.Id] = node;
            return this;
        }

        public FakeTree AddEpisodes(MediaTreeNode parent, params MediaTreeNode[] episodes)
        {
            _nodes[parent.Id] = parent;
            _children[parent.Id] = episodes.ToList();
            foreach (var episode in episodes)
            {
                _nodes[episode.Id] = episode;
            }

            return this;
        }

        public MediaTreeNode? GetNode(Guid id) => _nodes.TryGetValue(id, out var node) ? node : null;

        public IReadOnlyList<MediaTreeNode> GetEpisodes(Guid parentId, bool recursive)
        {
            if (!_children.TryGetValue(parentId, out var episodes))
            {
                return Array.Empty<MediaTreeNode>();
            }

            return episodes;
        }
    }

    private static readonly MediaTreeNode Movie = new(Guid.Parse("11111111-1111-1111-1111-111111111111"), "Heat", "Movie");
    private static readonly MediaTreeNode Show = new(Guid.Parse("22222222-2222-2222-2222-222222222222"), "The Wire", "Series");
    private static readonly MediaTreeNode SeasonOne = new(
        Guid.Parse("33333333-3333-3333-3333-333333333333"), "Season 1", "Season", "The Wire", 1);
    private static readonly MediaTreeNode SeasonTwo = new(
        Guid.Parse("44444444-4444-4444-4444-444444444444"), "Season 2", "Season", "The Wire", 2);

    private static MediaTreeNode Episode(string suffix, int season, int number) => new(
        Guid.Parse("66666666-6666-6666-6666-6666666666" + suffix),
        "Episode " + number,
        "Episode",
        "The Wire",
        season,
        number);

    private static readonly MediaTreeNode S1E1 = Episode("01", 1, 1);
    private static readonly MediaTreeNode S1E2 = Episode("02", 1, 2);
    private static readonly MediaTreeNode S2E1 = Episode("03", 2, 1);

    private static FakeTree Tree()
    {
        var tree = new FakeTree()
            .Add(Movie)
            .Add(Show)
            .AddEpisodes(SeasonOne, S1E1, S1E2)
            .AddEpisodes(SeasonTwo, S2E1);

        // The show's recursive episode list is what the real source returns for a series.
        tree.AddEpisodes(Show, S1E1, S1E2, S2E1);
        return tree;
    }

    [Fact]
    public void MovieSelection_QueuesTheMovieItself()
    {
        var expansion = MediaTreeExpansion.Expand(Tree(), new[] { Movie.Id });

        Assert.Single(expansion.Items);
        Assert.Equal(Movie.Id, expansion.Items[0].Id);
        Assert.Empty(expansion.MissingItemIds);
        Assert.Empty(expansion.Problems);
    }

    [Fact]
    public void SeriesSelection_ExpandsToEveryEpisode()
    {
        var expansion = MediaTreeExpansion.Expand(Tree(), new[] { Show.Id });

        Assert.Equal(new[] { S1E1.Id, S1E2.Id, S2E1.Id }, expansion.Items.Select(item => item.Id));
    }

    [Fact]
    public void SeasonSelection_ExpandsToThatSeasonOnly()
    {
        var expansion = MediaTreeExpansion.Expand(Tree(), new[] { SeasonOne.Id });

        Assert.Equal(new[] { S1E1.Id, S1E2.Id }, expansion.Items.Select(item => item.Id));
        Assert.DoesNotContain(expansion.Items, item => item.Id == S2E1.Id);
    }

    [Fact]
    public void SeriesAndItsSeason_DoNotQueueTheSameEpisodeTwice()
    {
        var expansion = MediaTreeExpansion.Expand(Tree(), new[] { Show.Id, SeasonOne.Id });

        Assert.Equal(new[] { S1E1.Id, S1E2.Id, S2E1.Id }, expansion.Items.Select(item => item.Id));
    }

    [Fact]
    public void MixedSelection_KeepsSelectionOrderAndExpandsContainers()
    {
        var expansion = MediaTreeExpansion.Expand(Tree(), new[] { Movie.Id, SeasonTwo.Id });

        Assert.Equal(new[] { Movie.Id, S2E1.Id }, expansion.Items.Select(item => item.Id));
    }

    [Fact]
    public void EmptySeries_IsReportedAsAProblem()
    {
        var emptyShow = new MediaTreeNode(Guid.NewGuid(), "Frasier", "Series");
        var tree = new FakeTree().Add(emptyShow);

        var expansion = MediaTreeExpansion.Expand(tree, new[] { emptyShow.Id });

        Assert.Empty(expansion.Items);
        var problem = Assert.Single(expansion.Problems);
        Assert.Equal(emptyShow.Id, problem.ItemId);
        Assert.Contains("no episodes", problem.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnknownId_IsReportedAsMissing()
    {
        var unknown = Guid.NewGuid();

        var expansion = MediaTreeExpansion.Expand(Tree(), new[] { unknown });

        Assert.Empty(expansion.Items);
        Assert.Equal(new[] { unknown }, expansion.MissingItemIds);
        Assert.Empty(expansion.Problems);
    }

    [Fact]
    public void UnsupportedType_IsReportedAsAProblem()
    {
        var album = new MediaTreeNode(Guid.NewGuid(), "Kid A", "MusicAlbum");
        var tree = new FakeTree().Add(album);

        var expansion = MediaTreeExpansion.Expand(tree, new[] { album.Id });

        Assert.Empty(expansion.Items);
        var problem = Assert.Single(expansion.Problems);
        Assert.Contains("MusicAlbum", problem.Message);
    }

    [Fact]
    public void EmptySelection_ExpandsToNothing()
    {
        var expansion = MediaTreeExpansion.Expand(Tree(), Array.Empty<Guid>());

        Assert.Empty(expansion.Items);
        Assert.Empty(expansion.MissingItemIds);
        Assert.Empty(expansion.Problems);
    }

    [Fact]
    public void NullSelection_ExpandsToNothing()
    {
        var expansion = MediaTreeExpansion.Expand(Tree(), null);

        Assert.Same(MediaExpansion.Empty, expansion);
    }

    [Fact]
    public void DuplicateIdsInSelection_AreCollapsed()
    {
        var expansion = MediaTreeExpansion.Expand(Tree(), new[] { Movie.Id, Movie.Id });

        Assert.Single(expansion.Items);
    }
}
