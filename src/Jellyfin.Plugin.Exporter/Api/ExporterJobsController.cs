using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.Exporter.Models;
using Jellyfin.Plugin.Exporter.Services;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Exporter.Api;

/// <summary>
/// Queue and job-history API behind the Export Status page: what is queued or
/// running, what finished, and the two actions the page offers (cancel, retry).
/// </summary>
/// <remarks>
/// Every field the status page needs is returned as a plain string or number so
/// the page does not have to guess enum casing or timestamp formats between
/// Jellyfin releases. Job ids are returned in the dashed form (and accepted in
/// either form) because Jellyfin serializes Guids without dashes in some
/// payloads.
/// </remarks>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("JellyfinExporter/Jobs")]
public class ExporterJobsController : ControllerBase
{
    private readonly IExportQueue _queue;

    /// <summary>
    /// Initializes a new instance of the <see cref="ExporterJobsController"/>
    /// class. Used when the plugin's services are not registered yet.
    /// </summary>
    public ExporterJobsController()
        : this(new ExportQueue())
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ExporterJobsController"/> class.
    /// </summary>
    /// <param name="queue">Export queue.</param>
    public ExporterJobsController(IExportQueue queue)
    {
        _queue = queue;
    }

    /// <summary>
    /// Lists jobs, newest activity first, with the counts the status page shows.
    /// </summary>
    /// <param name="status">
    /// Optional filter: a comma separated list of statuses (queued, running,
    /// completed, failed, cancelled) or one of the shorthands "active"
    /// (queued + running) and "finished" (completed + failed + cancelled).
    /// </param>
    /// <param name="limit">Optional maximum number of jobs to return.</param>
    /// <returns>The matching jobs and a summary of the whole queue.</returns>
    [HttpGet]
    public ActionResult<JobsResponse> Get([FromQuery] string? status = null, [FromQuery] int? limit = null)
    {
        var jobs = _queue.GetJobs();
        var all = jobs.Select(ExportJobView.From).ToList();

        var filtered = all.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(status))
        {
            var wanted = ParseStatusFilter(status);
            if (wanted.Count == 0)
            {
                return BadRequest(new
                {
                    message = $"Unknown status filter '{status}'. Use queued, running, completed, failed, cancelled, active or finished."
                });
            }

            filtered = filtered.Where(job => wanted.Contains(job.Status));
        }

        var ordered = filtered
            .OrderBy(job => IsActive(job.Status) ? 0 : 1)
            .ThenByDescending(job => job.Status == ExportJobStatus.Queued || job.Status == ExportJobStatus.Running
                ? job.CreatedAt
                : job.FinishedAt ?? job.CreatedAt)
            .ToList();

        if (limit is int max && max > 0)
        {
            ordered = ordered.Take(max).ToList();
        }

        var queue = _queue as ExportQueue;
        return new JobsResponse
        {
            Jobs = ordered,
            Counts = new JobCounts
            {
                Queued = all.Count(job => job.Status == ExportJobStatus.Queued),
                Running = all.Count(job => job.Status == ExportJobStatus.Running),
                Completed = all.Count(job => job.Status == ExportJobStatus.Completed),
                Failed = all.Count(job => job.Status == ExportJobStatus.Failed),
                Cancelled = all.Count(job => job.Status == ExportJobStatus.Cancelled),
                Total = all.Count
            },
            HistoryLimit = queue?.RetentionLimit ?? ExportQueue.DefaultRetentionLimit,
            HistoryFile = queue?.StorageLocation,
            StorageError = queue?.StorageError,
            ServerTimeUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)
        };
    }

    /// <summary>
    /// Cancels a queued or running job.
    /// </summary>
    /// <param name="id">Job id, dashed or undashed.</param>
    /// <returns>The job after the request, or 404 / 409.</returns>
    [HttpPost("{id}/cancel")]
    public ActionResult<ExportJobView> Cancel(string id)
    {
        if (!TryParseJobId(id, out var jobId))
        {
            return BadRequest(new { message = $"'{id}' is not a job id." });
        }

        var job = _queue.GetJobs().FirstOrDefault(candidate => candidate.Id == jobId);
        if (job is null)
        {
            return NotFound(new { message = $"Job {jobId} is not in the queue history." });
        }

        if (job.IsTerminal)
        {
            return Conflict(new { message = $"Job {jobId} already finished as {job.Status}." });
        }

        if (!_queue.Cancel(jobId))
        {
            return Conflict(new { message = $"Job {jobId} could not be cancelled." });
        }

        return ExportJobView.From(_queue.GetJobs().First(candidate => candidate.Id == jobId));
    }

    /// <summary>
    /// Queues a finished job again with the same item, profile snapshot and
    /// output directory.
    /// </summary>
    /// <param name="id">Id of the finished job to repeat.</param>
    /// <returns>The queued copy, or 404 / 409.</returns>
    [HttpPost("{id}/retry")]
    public ActionResult<ExportJobView> Retry(string id)
    {
        if (!TryParseJobId(id, out var jobId))
        {
            return BadRequest(new { message = $"'{id}' is not a job id." });
        }

        var jobs = _queue.GetJobs();
        var source = jobs.FirstOrDefault(candidate => candidate.Id == jobId);
        if (source is null)
        {
            return NotFound(new { message = $"Job {jobId} is not in the queue history." });
        }

        if (!source.IsTerminal)
        {
            return Conflict(new { message = $"Job {jobId} is still {source.Status}; cancel it instead of retrying it." });
        }

        // The same guard the export page uses: two active jobs for one item with
        // one profile would write the same file.
        var duplicate = jobs.FirstOrDefault(candidate =>
            !candidate.IsTerminal &&
            candidate.JellyfinItemId == source.JellyfinItemId &&
            string.Equals(candidate.ProfileName, source.ProfileName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(candidate.OutputDirectory, source.OutputDirectory, StringComparison.OrdinalIgnoreCase));

        if (duplicate is not null)
        {
            return Conflict(new
            {
                message = $"'{source.ItemName}' is already queued as job {duplicate.Id} for profile '{source.ProfileName}'."
            });
        }

        var retry = new ExportJob
        {
            JellyfinItemId = source.JellyfinItemId,
            ItemName = source.ItemName,
            ItemType = source.ItemType,
            ProfileId = source.ProfileId,
            ProfileName = source.ProfileName,
            Format = source.Format,
            Resolution = source.Resolution,
            AudioChannels = source.AudioChannels,
            OutputDirectory = source.OutputDirectory,
            CreatedAt = DateTimeOffset.UtcNow
        };

        var added = _queue.Enqueue(new[] { retry });
        if (added.Count == 0)
        {
            return Conflict(new { message = "The job could not be queued again." });
        }

        return ExportJobView.From(added[0]);
    }

    private static bool IsActive(ExportJobStatus status) =>
        status is ExportJobStatus.Queued or ExportJobStatus.Running;

    private static HashSet<ExportJobStatus> ParseStatusFilter(string filter)
    {
        var wanted = new HashSet<ExportJobStatus>();
        foreach (var raw in filter.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            switch (raw.Trim().ToLowerInvariant())
            {
                case "active":
                    wanted.Add(ExportJobStatus.Queued);
                    wanted.Add(ExportJobStatus.Running);
                    break;
                case "finished":
                    wanted.Add(ExportJobStatus.Completed);
                    wanted.Add(ExportJobStatus.Failed);
                    wanted.Add(ExportJobStatus.Cancelled);
                    break;
                case "queued":
                    wanted.Add(ExportJobStatus.Queued);
                    break;
                case "running":
                    wanted.Add(ExportJobStatus.Running);
                    break;
                case "completed":
                case "complete":
                case "success":
                    wanted.Add(ExportJobStatus.Completed);
                    break;
                case "failed":
                case "failure":
                case "error":
                    wanted.Add(ExportJobStatus.Failed);
                    break;
                case "cancelled":
                case "canceled":
                    wanted.Add(ExportJobStatus.Cancelled);
                    break;
                default:
                    return new HashSet<ExportJobStatus>();
            }
        }

        return wanted;
    }

    private static bool TryParseJobId(string raw, out Guid jobId)
    {
        jobId = Guid.Empty;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        return (Guid.TryParse(raw, out jobId) || Guid.TryParseExact(raw, "N", out jobId)) && jobId != Guid.Empty;
    }
}

/// <summary>
/// Response for <see cref="ExporterJobsController.Get"/>.
/// </summary>
public class JobsResponse
{
    /// <summary>Gets or sets the matching jobs, active first.</summary>
    public IReadOnlyList<ExportJobView> Jobs { get; set; } = Array.Empty<ExportJobView>();

    /// <summary>Gets or sets the counts for the whole queue history.</summary>
    public JobCounts Counts { get; set; } = new();

    /// <summary>Gets or sets how many finished jobs are retained.</summary>
    public int HistoryLimit { get; set; }

    /// <summary>Gets or sets the job history file path, when it is persisted.</summary>
    public string? HistoryFile { get; set; }

    /// <summary>Gets or sets the last persistence problem, when there is one.</summary>
    public string? StorageError { get; set; }

    /// <summary>Gets or sets the server time, so the page can show relative ages.</summary>
    public string ServerTimeUtc { get; set; } = string.Empty;
}

/// <summary>
/// Job counts by status.
/// </summary>
public class JobCounts
{
    /// <summary>Gets or sets the number of queued jobs.</summary>
    public int Queued { get; set; }

    /// <summary>Gets or sets the number of running jobs.</summary>
    public int Running { get; set; }

    /// <summary>Gets or sets the number of completed jobs.</summary>
    public int Completed { get; set; }

    /// <summary>Gets or sets the number of failed jobs.</summary>
    public int Failed { get; set; }

    /// <summary>Gets or sets the number of cancelled jobs.</summary>
    public int Cancelled { get; set; }

    /// <summary>Gets or sets the total number of jobs in the history.</summary>
    public int Total { get; set; }
}

/// <summary>
/// One job as the status page renders it.
/// </summary>
public class ExportJobView
{
    /// <summary>Gets or sets the job id (dashed form).</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the Jellyfin item id (dashed form).</summary>
    public string JellyfinItemId { get; set; } = string.Empty;

    /// <summary>Gets or sets the item name.</summary>
    public string ItemName { get; set; } = string.Empty;

    /// <summary>Gets or sets the item type (Movie, Episode, ...).</summary>
    public string ItemType { get; set; } = string.Empty;

    /// <summary>Gets or sets the profile name captured when the job was queued.</summary>
    public string ProfileName { get; set; } = string.Empty;

    /// <summary>Gets or sets the codec.</summary>
    public string Format { get; set; } = string.Empty;

    /// <summary>Gets or sets the resolution.</summary>
    public string Resolution { get; set; } = string.Empty;

    /// <summary>Gets or sets the audio handling.</summary>
    public string AudioChannels { get; set; } = string.Empty;

    /// <summary>Gets or sets the output directory.</summary>
    public string OutputDirectory { get; set; } = string.Empty;

    /// <summary>Gets or sets the status as an enum member name.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ExportJobStatus Status { get; set; }

    /// <summary>Gets or sets progress in percent (0-100).</summary>
    public double ProgressPercent { get; set; }

    /// <summary>Gets or sets the final output path.</summary>
    public string? OutputPath { get; set; }

    /// <summary>Gets or sets the error captured for a failed job.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Gets or sets when the job was queued (ISO 8601 UTC).</summary>
    public string CreatedAt { get; set; } = string.Empty;

    /// <summary>Gets or sets when the export started (ISO 8601 UTC).</summary>
    public string? StartedAt { get; set; }

    /// <summary>Gets or sets when the job finished (ISO 8601 UTC).</summary>
    public string? FinishedAt { get; set; }

    /// <summary>Gets or sets the elapsed seconds of a finished or running export.</summary>
    public double? ElapsedSeconds { get; set; }

    /// <summary>Gets or sets a value indicating whether the page may offer cancel.</summary>
    public bool CanCancel { get; set; }

    /// <summary>Gets or sets a value indicating whether the page may offer retry.</summary>
    public bool CanRetry { get; set; }

    /// <summary>
    /// Projects a job onto the response shape.
    /// </summary>
    /// <param name="job">Job.</param>
    /// <returns>The response view.</returns>
    public static ExportJobView From(ExportJob job)
    {
        var end = job.FinishedAt ?? DateTimeOffset.UtcNow;
        double? elapsed = job.StartedAt is DateTimeOffset started
            ? Math.Max(0, (end - started).TotalSeconds)
            : null;

        return new ExportJobView
        {
            Id = job.Id.ToString("D"),
            JellyfinItemId = job.JellyfinItemId.ToString("D"),
            ItemName = job.ItemName ?? string.Empty,
            ItemType = job.ItemType ?? string.Empty,
            ProfileName = job.ProfileName ?? string.Empty,
            Format = job.Format.ToString(),
            Resolution = job.Resolution.ToString(),
            AudioChannels = job.AudioChannels.ToString(),
            OutputDirectory = job.OutputDirectory ?? string.Empty,
            Status = job.Status,
            ProgressPercent = Math.Round(Math.Clamp(job.ProgressPercent, 0d, 100d), 1),
            OutputPath = job.OutputPath,
            ErrorMessage = job.ErrorMessage,
            CreatedAt = job.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
            StartedAt = job.StartedAt?.ToString("O", CultureInfo.InvariantCulture),
            FinishedAt = job.FinishedAt?.ToString("O", CultureInfo.InvariantCulture),
            ElapsedSeconds = elapsed,
            CanCancel = !job.IsTerminal,
            CanRetry = job.IsTerminal
        };
    }
}
