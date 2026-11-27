using System;
using System.Collections.Generic;
using System.IO;

namespace Jellyfin.Plugin.Exporter.Export;

/// <summary>
/// Hands out unique output paths to concurrent jobs. Two exports that resolve
/// to the same name must not write into each other's file while the first one
/// is still encoding, so a path is reserved in memory before the temp file is
/// created and released when the job reaches a terminal state.
/// </summary>
public sealed class PathReservationRegistry
{
    private readonly object _gate = new();
    private readonly HashSet<string> _reserved = new(StringComparer.Ordinal);

    /// <summary>
    /// Reserves the requested path, or the first free <c>Title (n).ext</c>
    /// variant when the file already exists or is reserved by another job.
    /// </summary>
    /// <param name="desiredPath">Path computed from the layout contract.</param>
    /// <returns>An absolute path no other job is using.</returns>
    public string ReserveUnique(string desiredPath)
    {
        if (string.IsNullOrWhiteSpace(desiredPath))
        {
            throw new ExportException("The output path is empty.");
        }

        var candidate = Path.GetFullPath(desiredPath);
        lock (_gate)
        {
            for (var copy = 0; ; copy++)
            {
                var attempt = copy == 0 ? candidate : OutputLayout.AddDuplicateSuffix(candidate, copy);
                if (_reserved.Contains(attempt))
                {
                    continue;
                }

                if (File.Exists(attempt) || Directory.Exists(attempt))
                {
                    continue;
                }

                _reserved.Add(attempt);
                return attempt;
            }
        }
    }

    /// <summary>
    /// Reports whether another job currently holds a reservation inside
    /// <paramref name="directory"/>, so cleanup never removes a folder a
    /// sibling export is about to write into.
    /// </summary>
    /// <param name="directory">Folder about to be removed.</param>
    /// <param name="exceptPath">The caller's own reserved path, if any.</param>
    /// <returns><c>true</c> when a different job's reservation lives in the folder.</returns>
    public bool IsReservedInside(string directory, string? exceptPath = null)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }

        var folder = Path.GetFullPath(directory);
        var own = string.IsNullOrWhiteSpace(exceptPath) ? null : Path.GetFullPath(exceptPath);
        lock (_gate)
        {
            foreach (var reserved in _reserved)
            {
                if (own is not null && string.Equals(reserved, own, StringComparison.Ordinal))
                {
                    continue;
                }

                if (IsInside(reserved, folder))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsInside(string path, string folder)
    {
        if (string.Equals(path, folder, StringComparison.Ordinal))
        {
            return true;
        }

        var relative = Path.GetRelativePath(folder, path);
        return !Path.IsPathRooted(relative)
            && !string.Equals(relative, "..", StringComparison.Ordinal)
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    /// <summary>
    /// Releases a reservation. Called when the job finishes, fails or is
    /// cancelled; a completed export is protected by the file on disk.
    /// </summary>
    /// <param name="path">Path returned by <see cref="ReserveUnique"/>.</param>
    public void Release(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        lock (_gate)
        {
            _reserved.Remove(Path.GetFullPath(path));
        }
    }
}
