namespace Jellyfin.Plugin.Exporter.Models;

/// <summary>
/// Lifecycle state of a single export job.
/// </summary>
public enum ExportJobStatus
{
    /// <summary>Waiting for a worker slot.</summary>
    Queued = 0,

    /// <summary>FFmpeg is running.</summary>
    Running = 1,

    /// <summary>Finished; output file is in place.</summary>
    Completed = 2,

    /// <summary>FFmpeg or the file-system step failed.</summary>
    Failed = 3,

    /// <summary>Cancelled by an operator (or during shutdown).</summary>
    Cancelled = 4
}
