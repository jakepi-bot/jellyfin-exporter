using Jellyfin.Plugin.Exporter.Export;

namespace Jellyfin.Plugin.Exporter.Tests;

/// <summary>
/// Folder tracking for the "no artifacts at all" rule: a failed or cancelled
/// export must not leave an empty directory skeleton behind, and must never
/// delete a folder that holds another job's file or that existed before the job.
/// </summary>
public class OutputTreeCleanupTests
{
    [Fact]
    public void CreateTrackedDirectory_ReturnsOnlyTheFoldersTheJobCreated()
    {
        using var directory = new TestMedia.TempDirectory();
        var root = directory.Combine("exports");
        Directory.CreateDirectory(Path.Combine(root, "Movies"));

        var created = OutputTreeCleanup.CreateTrackedDirectory(Path.Combine(root, "Movies", "A Movie (2020)"), root);

        Assert.Equal(new[] { Path.Combine(root, "Movies", "A Movie (2020)") }, created);
        Assert.True(Directory.Exists(created[0]));
    }

    [Fact]
    public void CreateTrackedDirectory_IncludesTheOutputDirectoryButNeverItsParent()
    {
        using var directory = new TestMedia.TempDirectory();
        var root = directory.Combine("exports", "nested");
        var target = Path.Combine(root, "TV", "Show", "Season 01");

        var created = OutputTreeCleanup.CreateTrackedDirectory(target, root);

        Assert.Equal(
            new[] { target, Path.Combine(root, "TV", "Show"), Path.Combine(root, "TV"), root },
            created);
        Assert.DoesNotContain(directory.Combine("exports"), created);
    }

    [Fact]
    public void PruneEmptyDirectories_RemovesTheCreatedChainDeepestFirst()
    {
        using var directory = new TestMedia.TempDirectory();
        var root = directory.Combine("exports");
        var target = Path.Combine(root, "TV", "Show", "Season 01");
        var created = OutputTreeCleanup.CreateTrackedDirectory(target, root);

        var removed = OutputTreeCleanup.PruneEmptyDirectories(created);

        Assert.Equal(created, removed);
        Assert.False(Directory.Exists(root));
        Assert.True(Directory.Exists(directory.Path));
    }

    [Fact]
    public void PruneEmptyDirectories_StopsAtAFolderHoldingAnotherJobsFile()
    {
        using var directory = new TestMedia.TempDirectory();
        var root = directory.Combine("exports");
        var target = Path.Combine(root, "TV", "Show", "Season 01");
        var created = OutputTreeCleanup.CreateTrackedDirectory(target, root);

        // Another job wrote into the same season folder while this one ran.
        var otherJob = Path.Combine(target, "02 - Other Episode.mkv");
        File.WriteAllText(otherJob, "keep me");

        var removed = OutputTreeCleanup.PruneEmptyDirectories(created);

        Assert.Empty(removed);
        Assert.True(Directory.Exists(target));
        Assert.Equal("keep me", File.ReadAllText(otherJob));
        Assert.True(Directory.Exists(Path.Combine(root, "TV", "Show")));
    }

    [Fact]
    public void PruneEmptyDirectories_LeavesAPreExistingFolderAlone()
    {
        using var directory = new TestMedia.TempDirectory();
        var root = directory.Combine("exports");
        var shared = Path.Combine(root, "Movies");
        Directory.CreateDirectory(shared);

        var created = OutputTreeCleanup.CreateTrackedDirectory(Path.Combine(shared, "A Movie (2020)"), root);
        var removed = OutputTreeCleanup.PruneEmptyDirectories(created);

        Assert.Equal(new[] { Path.Combine(shared, "A Movie (2020)") }, removed);
        Assert.True(Directory.Exists(shared));
    }

    [Fact]
    public void PruneEmptyDirectories_SkipsFoldersThatAreAlreadyGone()
    {
        using var directory = new TestMedia.TempDirectory();
        var root = directory.Combine("exports");
        Directory.CreateDirectory(root);
        var missing = Path.Combine(root, "Movies", "Gone (2020)");

        var removed = OutputTreeCleanup.PruneEmptyDirectories(new[] { missing, root });

        Assert.Equal(new[] { root }, removed);
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void CreateTrackedDirectory_RefusesAPathOutsideTheOutputDirectory()
    {
        using var directory = new TestMedia.TempDirectory();
        var root = directory.Combine("exports");
        Directory.CreateDirectory(root);

        var outside = directory.Combine("somewhere-else", "A Movie (2020)");

        var error = Assert.Throws<ExportException>(() => OutputTreeCleanup.CreateTrackedDirectory(outside, root));
        Assert.Contains("outside the job's output directory", error.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(outside));
    }

    [Fact]
    public void CreateTrackedDirectory_RefusesARelativeOutputDirectory()
    {
        using var directory = new TestMedia.TempDirectory();
        var target = directory.Combine("exports", "Movies", "A Movie (2020)");

        Assert.Throws<ExportException>(() => OutputTreeCleanup.CreateTrackedDirectory(target, "exports"));
        Assert.False(Directory.Exists(target));
    }
}
