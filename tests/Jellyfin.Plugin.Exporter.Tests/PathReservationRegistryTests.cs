using Jellyfin.Plugin.Exporter.Export;

namespace Jellyfin.Plugin.Exporter.Tests;

/// <summary>
/// Reservations exist so two jobs never write the same file. Cleanup also asks
/// the registry whether a folder is claimed, because an empty folder can still
/// be the destination of a sibling job that has not started writing.
/// </summary>
public class PathReservationRegistryTests
{
    [Fact]
    public void ReserveUnique_HandsOutAStartedPathThatItReportsAsReserved()
    {
        using var directory = new TestMedia.TempDirectory();
        var registry = new PathReservationRegistry();
        var desired = Path.Combine(directory.Path, "TV", "Show", "Season 01", "01 - Pilot.mkv");

        var reserved = registry.ReserveUnique(desired);

        Assert.Equal(desired, reserved);
        Assert.True(registry.IsReservedInside(Path.GetDirectoryName(desired)!));
        Assert.True(registry.IsReservedInside(directory.Path));
    }

    [Fact]
    public void IsReservedInside_IsFalseForUnrelatedFolders()
    {
        using var directory = new TestMedia.TempDirectory();
        var registry = new PathReservationRegistry();
        registry.ReserveUnique(Path.Combine(directory.Path, "TV", "Show A", "Season 01", "01.mkv"));

        Assert.False(registry.IsReservedInside(Path.Combine(directory.Path, "TV", "Show B", "Season 01")));
        Assert.False(registry.IsReservedInside(Path.Combine(directory.Path, "Movies")));
    }

    [Fact]
    public void IsReservedInside_ExcludesTheCallersOwnPath()
    {
        using var directory = new TestMedia.TempDirectory();
        var registry = new PathReservationRegistry();
        var season = Path.Combine(directory.Path, "TV", "Show", "Season 01");
        var own = registry.ReserveUnique(Path.Combine(season, "01 - Pilot.mkv"));

        // The job pruning its own failed folder must not count itself as a
        // reason to keep it.
        Assert.False(registry.IsReservedInside(season, own));
        Assert.True(registry.IsReservedInside(season));
    }

    [Fact]
    public void IsReservedInside_SeesASiblingReservationInTheSameFolder()
    {
        using var directory = new TestMedia.TempDirectory();
        var registry = new PathReservationRegistry();
        var season = Path.Combine(directory.Path, "TV", "Show", "Season 01");
        var own = registry.ReserveUnique(Path.Combine(season, "01 - Pilot.mkv"));
        registry.ReserveUnique(Path.Combine(season, "02 - Second.mkv"));

        Assert.True(registry.IsReservedInside(season, own));

        registry.Release(Path.Combine(season, "02 - Second.mkv"));
        Assert.False(registry.IsReservedInside(season, own));
    }

    [Fact]
    public void IsReservedInside_IsFalseForBlankFolders()
    {
        var registry = new PathReservationRegistry();
        Assert.False(registry.IsReservedInside(string.Empty));
        Assert.False(registry.IsReservedInside("   "));
    }
}
