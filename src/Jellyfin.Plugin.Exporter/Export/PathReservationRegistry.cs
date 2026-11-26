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
