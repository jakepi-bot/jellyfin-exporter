using System;

namespace Jellyfin.Plugin.Exporter.Export;

/// <summary>
/// Everything the export engine needs to know about the media item behind a
/// job: the file to read and the metadata that decides the output path.
/// </summary>
public sealed record ExportItemInfo
{
    /// <summary>
    /// Gets the absolute path of the source media file.
    /// </summary>
    public string SourcePath { get; init; } = string.Empty;

    /// <summary>
    /// Gets the title used in the file name: the movie title, or the episode
    /// name for an episode.
    /// </summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>
    /// Gets the movie's release year, when known.
    /// </summary>
    public int? Year { get; init; }

    /// <summary>
    /// Gets the series name for an episode.
    /// </summary>
    public string? ShowName { get; init; }

    /// <summary>
    /// Gets the season number for an episode (0 when Jellyfin has none).
    /// </summary>
    public int? SeasonNumber { get; init; }

    /// <summary>
    /// Gets the episode number inside the season (0 when Jellyfin has none).
    /// </summary>
    public int? EpisodeNumber { get; init; }

    /// <summary>
    /// Gets a value indicating whether this item is laid out under TV/.
    /// </summary>
    public bool IsEpisode => !string.IsNullOrWhiteSpace(ShowName) || SeasonNumber.HasValue || EpisodeNumber.HasValue;

    /// <summary>
    /// Creates the description of a movie.
    /// </summary>
    public static ExportItemInfo ForMovie(string sourcePath, string title, int? year) => new()
    {
        SourcePath = sourcePath,
        Title = title,
        Year = year
    };

    /// <summary>
    /// Creates the description of a TV episode.
    /// </summary>
    public static ExportItemInfo ForEpisode(
        string sourcePath,
        string showName,
        int? seasonNumber,
        int? episodeNumber,
        string episodeName) => new()
    {
        SourcePath = sourcePath,
        Title = episodeName,
        ShowName = showName,
        SeasonNumber = seasonNumber ?? 0,
        EpisodeNumber = episodeNumber ?? 0
    };

    /// <summary>
    /// Throws when the item cannot be exported.
    /// </summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(SourcePath))
        {
            throw new ExportException("The item has no source file path.");
        }

        if (string.IsNullOrWhiteSpace(Title))
        {
            throw new ExportException("The item has no name to build an output file name from.");
        }
    }
}
