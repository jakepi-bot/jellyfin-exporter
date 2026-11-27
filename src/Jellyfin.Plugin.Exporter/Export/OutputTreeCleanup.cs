using System;
using System.Collections.Generic;
using System.IO;

namespace Jellyfin.Plugin.Exporter.Export;

/// <summary>
/// Creates a job's destination folders and remembers which of them did not
/// exist, so a failed or cancelled export can leave the output tree the way it
/// found it instead of a skeleton of empty directories.
/// </summary>
/// <remarks>
/// Only directories this job created are ever removed, and only while they are
/// empty: a folder that already existed, or that holds another job's artifact,
/// is left alone. Nothing above the job's own output directory is touched.
/// </remarks>
public static class OutputTreeCleanup
{
    /// <summary>
    /// Safety stop for the ancestor walk, in case of a pathological path.
    /// </summary>
    public const int MaxTrackedDepth = 64;

    /// <summary>
    /// Creates <paramref name="directory"/> and every missing folder between it
    /// and <paramref name="outputRoot"/>.
    /// </summary>
    /// <param name="directory">Folder the export writes into.</param>
    /// <param name="outputRoot">The job's output directory (the edge of its tree).</param>
    /// <returns>The folders this call created, deepest first, for <see cref="PruneEmptyDirectories"/>.</returns>
    /// <exception cref="ExportException">The folder is not inside the output root.</exception>
    public static IReadOnlyList<string> CreateTrackedDirectory(string directory, string outputRoot)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new ExportException("The output path has no directory.");
        }

        var root = Path.GetFullPath(RequireRoot(outputRoot));
        var target = Path.GetFullPath(directory);
        if (!IsInside(target, root))
        {
            throw new ExportException($"The output folder '{target}' is outside the job's output directory '{root}'.");
        }

        var created = MissingAncestors(target, root);
        Directory.CreateDirectory(target);
        return created;
    }

    /// <summary>
    /// Deletes the folders recorded by <see cref="CreateTrackedDirectory"/>, deepest
    /// first, stopping at the first one that is no longer empty.
    /// </summary>
    /// <param name="createdDirectories">Deepest-first list from <see cref="CreateTrackedDirectory"/>.</param>
    /// <param name="isInUse">
    /// Optional check for a folder another export is about to write into. An
    /// empty folder can still be claimed by a sibling job that has reserved its
    /// output path but has not created the file yet, which is exactly the window
    /// where removing it breaks that job.
    /// </param>
    /// <returns>The folders that were removed.</returns>
    /// <remarks>
    /// A concurrent export writing into one of these folders makes it non-empty,
    /// which stops the walk; a folder deleted in the meantime is skipped.
    /// </remarks>
    public static IReadOnlyList<string> PruneEmptyDirectories(
        IEnumerable<string> createdDirectories,
        Func<string, bool>? isInUse = null)
    {
        ArgumentNullException.ThrowIfNull(createdDirectories);

        var removed = new List<string>();
        foreach (var path in createdDirectories)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            try
            {
                if (!Directory.Exists(path))
                {
                    continue;
                }

                if (!IsEmpty(path))
                {
                    // A parent cannot be empty while its child folder exists.
                    break;
                }

                if (isInUse is not null && SafeInUse(isInUse, path))
                {
                    // Another job reserved a file here: keep the folder and
                    // therefore every ancestor, even though it looks empty now.
                    break;
                }

                Directory.Delete(path, recursive: false);
                removed.Add(path);
            }
            catch (IOException)
            {
                break;
            }
            catch (UnauthorizedAccessException)
            {
                break;
            }
        }

        return removed;
    }

    private static string RequireRoot(string outputRoot)
    {
        if (string.IsNullOrWhiteSpace(outputRoot))
        {
            throw new ExportException("The job has no output directory.");
        }

        if (!Path.IsPathRooted(outputRoot))
        {
            throw new ExportException($"The output directory must be an absolute path: {outputRoot}");
        }

        return outputRoot;
    }

    private static List<string> MissingAncestors(string target, string root)
    {
        var created = new List<string>();
        var current = target;
        while (current is not null
            && created.Count < MaxTrackedDepth
            && IsInside(current, root))
        {
            if (Directory.Exists(current))
            {
                break;
            }

            created.Add(current);
            current = Path.GetDirectoryName(current);
        }

        return created;
    }

    private static bool IsInside(string path, string root)
    {
        if (string.Equals(path, root, StringComparison.Ordinal))
        {
            return true;
        }

        var relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative)
            && !string.Equals(relative, "..", StringComparison.Ordinal)
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static bool SafeInUse(Func<string, bool> isInUse, string path)
    {
        try
        {
            return isInUse(path);
        }
        catch (Exception)
        {
            // A failing guard must never delete a folder that might belong to
            // another job, so treat it as "in use".
            return true;
        }
    }

    private static bool IsEmpty(string path)
    {
        using var entries = Directory.EnumerateFileSystemEntries(path).GetEnumerator();
        return !entries.MoveNext();
    }
}
