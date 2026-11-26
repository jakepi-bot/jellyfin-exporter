using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace Jellyfin.Plugin.Exporter.Export;

/// <summary>
/// The output path contract:
/// <c>&lt;OutputDirectory&gt;/Movies/{Title} ({Year})/{Title} ({Year}).{ext}</c> and
/// <c>&lt;OutputDirectory&gt;/TV/{Show}/Season {NN}/{NN} - {Episode}.{ext}</c>.
/// Every name is sanitized for the file system and long names are truncated.
/// </summary>
public static class OutputLayout
{
    /// <summary>Folder that holds movie exports.</summary>
    public const string MoviesFolderName = "Movies";

    /// <summary>Folder that holds TV exports.</summary>
    public const string TvFolderName = "TV";

    /// <summary>Longest file or folder name the exporter writes.</summary>
    public const int MaxNameLength = 120;

    private static readonly char[] UnsafeCharacters = { '<', '>', ':', '"', '/', '\\', '|', '?', '*' };

    /// <summary>
    /// Makes a title safe to use as a file or folder name: unsafe characters
    /// and control characters become '-', whitespace collapses, leading and
    /// trailing dots and spaces are trimmed, and the result is length-capped.
    /// </summary>
    /// <param name="raw">Operator or metadata supplied name.</param>
    /// <param name="fallback">Name to use when nothing usable is left.</param>
    /// <returns>A non-empty, file-system-safe name.</returns>
    public static string SanitizeName(string? raw, string fallback = "Untitled")
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        var builder = new StringBuilder(raw.Length);
        foreach (var character in raw)
        {
            if (char.IsControl(character) || Array.IndexOf(UnsafeCharacters, character) >= 0)
            {
                builder.Append('-');
            }
            else
            {
                builder.Append(character);
            }
        }

        var collapsed = string.Join(' ', builder.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var trimmed = collapsed.Trim().TrimStart('.').TrimEnd('.', ' ').Trim();
        if (trimmed.Length > MaxNameLength)
        {
            trimmed = trimmed[..MaxNameLength].TrimEnd('.', ' ').Trim();
        }

        return trimmed.Length == 0 ? fallback : trimmed;
    }

    /// <summary>
    /// Builds the movie folder name, <c>{Title} ({Year})</c>, dropping the year
    /// when Jellyfin does not know it.
    /// </summary>
    /// <param name="item">Item being exported.</param>
    /// <returns>Folder and file base name for a movie.</returns>
    public static string GetMovieBaseName(ExportItemInfo item)
    {
        var title = SanitizeName(item.Title, "Untitled Movie");
        return item.Year is int year
            ? string.Format(CultureInfo.InvariantCulture, "{0} ({1})", title, year)
            : title;
    }

    /// <summary>
    /// Builds the season folder name, <c>Season {NN}</c>.
    /// </summary>
    /// <param name="item">Item being exported.</param>
    /// <returns>Season folder name.</returns>
    public static string GetSeasonFolderName(ExportItemInfo item) =>
        "Season " + (item.SeasonNumber ?? 0).ToString("D2", CultureInfo.InvariantCulture);

    /// <summary>
    /// Builds the episode file name, <c>{NN} - {Episode Name}.{ext}</c>.
    /// </summary>
    /// <param name="item">Item being exported.</param>
    /// <param name="extension">Output extension, with or without a dot.</param>
    /// <returns>Episode file name.</returns>
    public static string GetEpisodeFileName(ExportItemInfo item, string extension)
    {
        var number = (item.EpisodeNumber ?? 0).ToString("D2", CultureInfo.InvariantCulture);
        var episodeName = SanitizeName(item.Title, "Episode " + number);
        return string.Format(CultureInfo.InvariantCulture, "{0} - {1}.{2}", number, episodeName, NormalizeExtension(extension));
    }

    /// <summary>
    /// Builds the full movie path.
    /// </summary>
    /// <param name="outputDirectory">Profile output directory.</param>
    /// <param name="item">Item being exported.</param>
    /// <param name="extension">Output extension, with or without a dot.</param>
    /// <returns>Absolute output file path.</returns>
    public static string GetMovieFilePath(string outputDirectory, ExportItemInfo item, string extension)
    {
        var baseName = GetMovieBaseName(item);
        return Path.Combine(
            RequireDirectory(outputDirectory),
            MoviesFolderName,
            baseName,
            baseName + "." + NormalizeExtension(extension));
    }

    /// <summary>
    /// Builds the full episode path.
    /// </summary>
    /// <param name="outputDirectory">Profile output directory.</param>
    /// <param name="item">Item being exported.</param>
    /// <param name="extension">Output extension, with or without a dot.</param>
    /// <returns>Absolute output file path.</returns>
    public static string GetEpisodeFilePath(string outputDirectory, ExportItemInfo item, string extension)
    {
        return Path.Combine(
            RequireDirectory(outputDirectory),
            TvFolderName,
            SanitizeName(item.ShowName, "Unknown Show"),
            GetSeasonFolderName(item),
            GetEpisodeFileName(item, extension));
    }

    /// <summary>
    /// Builds the output path for whichever item kind the job carries.
    /// </summary>
    /// <param name="outputDirectory">Profile output directory.</param>
    /// <param name="item">Item being exported.</param>
    /// <param name="extension">Output extension, with or without a dot.</param>
    /// <returns>Absolute output file path.</returns>
    public static string GetFilePath(string outputDirectory, ExportItemInfo item, string extension) =>
        item.IsEpisode
            ? GetEpisodeFilePath(outputDirectory, item, extension)
            : GetMovieFilePath(outputDirectory, item, extension);

    /// <summary>
    /// Adds a de-duplication suffix to a path, <c>Title (1).mkv</c>.
    /// </summary>
    /// <param name="path">Path that already exists.</param>
    /// <param name="copyNumber">Suffix number, starting at 1.</param>
    /// <returns>Path with the suffix inserted before the extension.</returns>
    public static string AddDuplicateSuffix(string path, int copyNumber)
    {
        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        return Path.Combine(directory, string.Format(CultureInfo.InvariantCulture, "{0} ({1}){2}", name, copyNumber, extension));
    }

    /// <summary>
    /// Normalizes a container extension: no leading dot, lower case.
    /// </summary>
    /// <param name="extension">Extension such as ".mkv" or "MKV".</param>
    /// <returns>Normalized extension.</returns>
    public static string NormalizeExtension(string extension)
    {
        var trimmed = (extension ?? string.Empty).Trim().TrimStart('.').ToLowerInvariant();
        return trimmed.Length == 0 ? "mkv" : trimmed;
    }

    private static string RequireDirectory(string outputDirectory)
    {
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            throw new ExportException("The profile has no output directory.");
        }

        if (!Path.IsPathRooted(outputDirectory))
        {
            throw new ExportException($"The output directory must be an absolute path: {outputDirectory}");
        }

        return outputDirectory;
    }
}
