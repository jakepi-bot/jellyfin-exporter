using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Exporter.Models;

namespace Jellyfin.Plugin.Exporter.Services;

/// <summary>
/// Queue of export jobs. The FFmpeg worker drains it; the API controller and
/// dashboard pages read from it and enqueue new work.
/// </summary>
public interface IExportQueue
{
    /// <summary>
    /// Raised after a job is enqueued and on every subsequent status change.
    /// Implementations must raise it exactly once per transition and must not
    /// let a throwing subscriber break the queue.
    /// </summary>
    event EventHandler<ExportJobStatusChangedEventArgs>? JobStatusChanged;

    /// <summary>
    /// Adds jobs to the queue. Ids missing from the supplied jobs are assigned
    /// by the implementation before the jobs are returned.
    /// </summary>
    /// <returns>The queued jobs, in queue order.</returns>
    IReadOnlyList<ExportJob> Enqueue(IEnumerable<ExportJob> jobs);

    /// <summary>
    /// Gets a snapshot of all known jobs (queued, running and recently
    /// finished, subject to the implementation's retention limit).
    /// </summary>
    IReadOnlyList<ExportJob> GetJobs();

    /// <summary>
    /// Requests cancellation of a queued or running job.
    /// </summary>
    /// <returns><c>true</c> when the job existed and cancellation was requested.</returns>
    bool Cancel(Guid jobId);
}
