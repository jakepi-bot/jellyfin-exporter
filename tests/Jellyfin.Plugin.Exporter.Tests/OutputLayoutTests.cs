using Jellyfin.Plugin.Exporter.Export;

namespace Jellyfin.Plugin.Exporter.Tests;

public class OutputLayoutTests
{
    private static ExportItemInfo Movie(string title = "The Movie", int? year = 2020) =>
        ExportItemInfo.ForMovie("/media/movie.mkv", title, year);

    private static ExportItemInfo Episode(
        string show = "Test Show",
        string episode = "The Third",
        int season = 2,
        int episodeNumber = 3) =>
        ExportItemInfo.ForEpisode("/media/episode.mkv", show, season, episodeNumber, episode);

    [Theory]
    [InlineData("Simple", "Simple")]
    [InlineData("Bad/Name:With*Chars?", "Bad-Name-With-Chars-")]
    [InlineData("  spaced   out  ", "spaced out")]
    [InlineData("trailing dots...", "trailing dots")]
    [InlineData("...leading", "leading")]
    [InlineData("tab\tnewline\n", "tab-newline-")]
    public void SanitizeName_MakesFileSystemSafeNames(string input, string expected)
    {
        Assert.Equal(expected, OutputLayout.SanitizeName(input));
    }

    [Fact]
    public void SanitizeName_FallsBackWhenNothingIsUsable()
    {
        Assert.Equal("Untitled", OutputLayout.SanitizeName("   "));
        Assert.Equal("Untitled", OutputLayout.SanitizeName("..."));
        Assert.Equal("Unknown", OutputLayout.SanitizeName(null, "Unknown"));
    }

    [Fact]
    public void SanitizeName_TruncatesLongNames()
    {
        var name = new string('a', OutputLayout.MaxNameLength + 50);

        Assert.Equal(OutputLayout.MaxNameLength, OutputLayout.SanitizeName(name).Length);
    }

    [Fact]
    public void MoviePath_FollowsTheDocumentedLayout()
    {
        var path = OutputLayout.GetMovieFilePath("/exports", Movie("The Movie", 2020), "mkv");

        Assert.Equal(Path.Combine("/exports", "Movies", "The Movie (2020)", "The Movie (2020).mkv"), path);
    }

    [Fact]
    public void MoviePath_DropsTheYearWhenUnknown()
    {
        var path = OutputLayout.GetMovieFilePath("/exports", Movie("The Movie", null), ".MKV");

        Assert.Equal(Path.Combine("/exports", "Movies", "The Movie", "The Movie.mkv"), path);
    }

    [Theory]
    [InlineData("Test Movie (2020)")]
    [InlineData("Test Movie (2020) ")]
    public void MoviePath_DoesNotRepeatAYearAlreadyInTheTitle(string title)
    {
        var path = OutputLayout.GetMovieFilePath("/exports", Movie(title, 2020), "mkv");

        Assert.Equal(Path.Combine("/exports", "Movies", "Test Movie (2020)", "Test Movie (2020).mkv"), path);
    }

    [Fact]
    public void MoviePath_KeepsADifferentYearInTheTitle()
    {
        var path = OutputLayout.GetMovieFilePath("/exports", Movie("Blade Runner (1982)", 2049), "mkv");

        Assert.Equal(
            Path.Combine("/exports", "Movies", "Blade Runner (1982) (2049)", "Blade Runner (1982) (2049).mkv"),
            path);
    }

    [Fact]
    public void EpisodePath_FollowsTheDocumentedLayoutWithZeroPadding()
    {
        var path = OutputLayout.GetEpisodeFilePath("/exports", Episode(season: 2, episodeNumber: 3), "mkv");

        Assert.Equal(
            Path.Combine("/exports", "TV", "Test Show", "Season 02", "03 - The Third.mkv"),
            path);
    }

    [Fact]
    public void EpisodePath_DoesNotTruncateNumbersAboveNinetyNine()
    {
        var path = OutputLayout.GetEpisodeFilePath("/exports", Episode(season: 12, episodeNumber: 105), "mkv");

        Assert.Equal(
            Path.Combine("/exports", "TV", "Test Show", "Season 12", "105 - The Third.mkv"),
            path);
    }

    [Fact]
    public void EpisodePath_SanitizesShowAndEpisodeNames()
    {
        var item = Episode(show: "Show: Name", episode: "Pilot/Part 1");

        var path = OutputLayout.GetEpisodeFilePath("/exports", item, "mkv");

        Assert.Equal(
            Path.Combine("/exports", "TV", "Show- Name", "Season 02", "03 - Pilot-Part 1.mkv"),
            path);
    }

    [Fact]
    public void GetFilePath_ChoosesTheTreeFromTheItem()
    {
        Assert.Contains(Path.Combine("Movies", "The Movie (2020)"), OutputLayout.GetFilePath("/exports", Movie(), "mkv"));
        Assert.Contains(Path.Combine("TV", "Test Show"), OutputLayout.GetFilePath("/exports", Episode(), "mkv"));
    }

    [Fact]
    public void GetFilePath_RejectsMissingOrRelativeDirectories()
    {
        Assert.Throws<ExportException>(() => OutputLayout.GetFilePath(string.Empty, Movie(), "mkv"));
        Assert.Throws<ExportException>(() => OutputLayout.GetFilePath("relative/path", Movie(), "mkv"));
    }

    [Fact]
    public void AddDuplicateSuffix_InsertsTheNumberBeforeTheExtension()
    {
        var path = Path.Combine("/exports", "Movies", "The Movie (2020)", "The Movie (2020).mkv");

        Assert.Equal(
            Path.Combine("/exports", "Movies", "The Movie (2020)", "The Movie (2020) (1).mkv"),
            OutputLayout.AddDuplicateSuffix(path, 1));
    }

    [Fact]
    public void PathReservation_SkipsFilesOnDiskAndOtherReservations()
    {
        var registry = new PathReservationRegistry();
        using var directory = new TestMedia.TempDirectory();
        var first = Path.Combine(directory.Path, "Title.mkv");
        File.WriteAllText(first, "already here");

        Assert.Equal(Path.Combine(directory.Path, "Title (1).mkv"), registry.ReserveUnique(first));

        var second = registry.ReserveUnique(Path.Combine(directory.Path, "Other.mkv"));
        var third = registry.ReserveUnique(Path.Combine(directory.Path, "Other.mkv"));

        Assert.Equal(Path.Combine(directory.Path, "Other.mkv"), second);
        Assert.Equal(Path.Combine(directory.Path, "Other (1).mkv"), third);

        registry.Release(second);
        Assert.Equal(second, registry.ReserveUnique(Path.Combine(directory.Path, "Other.mkv")));
    }
}
