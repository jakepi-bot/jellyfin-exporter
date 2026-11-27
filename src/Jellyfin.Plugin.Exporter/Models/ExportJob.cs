using System;

namespace Jellyfin.Plugin.Exporter.Models;

/// <summary>
/// One unit of export work: a single media item encoded with a single profile.
/// A series or season selection is expanded into one job per episode/movie
/// before it reaches the queue.
/// </summary>
public class ExportJob
{
    /// <summary>
    /// Gets or sets the job id, assigned when the job is created.
    /// </summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Gets or sets the Jellyfin item id this job exports.
    /// </summary>
    public Guid JellyfinItemId { get; set; }

    /// <summary>
    /// Gets or sets the display name of the item (for logs, UI and
    /// notifications) captured when the job was created.
    /// </summary>
    public string ItemName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Jellyfin item type, e.g. "Movie" or "Episode".
    /// </summary>
    public string ItemType { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the id of the profile used for this job.
    /// </summary>
    public Guid ProfileId { get; set; }

    /// <summary>
    /// Gets or sets the profile name captured when the job was created.
    /// </summary>
    public string ProfileName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the current status.
    /// </summary>
    public ExportJobStatus Status { get; set; } = ExportJobStatus.Queued;

    /// <summary>
    /// Gets or sets progress in percent (0-100) as reported by FFmpeg.
    /// </summary>
    public double ProgressPercent { get; set; }

    /// <summary>
    /// Gets or sets the final output file path once the job completes.
    /// </summary>
    public string? OutputPath { get; set; }

    /// <summary>
    /// Gets or sets a human-readable error captured from FFmpeg on failure.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Gets or sets when the job was queued (UTC).
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Gets or sets when a worker started the job (UTC).
    /// </summary>
    public DateTimeOffset? StartedAt { get; set; }

    /// <summary>
    /// Gets or sets when the job reached a terminal state (UTC).
    /// </summary>
    public DateTimeOffset? FinishedAt { get; set; }

    // Snapshot of the profile at enqueue time, so a job keeps its parameters
    // even if the profile is edited or deleted while the job is running.
    public ExportFormat Format { get; set; }

    public ExportResolution Resolution { get; set; }

    public ExportAudioChannels AudioChannels { get; set; }

    public string OutputDirectory { get; set; } = string.Empty;

    /// <summary>
    /// Snapshot of the profile's <c>UseJellyfinTranscodingSettings</c> toggle,
    /// so a queued job keeps its encoder-settings behaviour when the profile
    /// is edited while the job waits in the queue.
    /// </summary>
    public bool UseJellyfinTranscodingSettings { get; set; } = true;

    /// <summary>
    /// Gets a value indicating whether the job is in a terminal state.
    /// </summary>
    public bool IsTerminal => Status is ExportJobStatus.Completed or ExportJobStatus.Failed or ExportJobStatus.Cancelled;

    /// <summary>
    /// Returns an independent copy. The queue hands copies to readers and
    /// event subscribers so a caller cannot mutate the queued job.
    /// </summary>
    /// <returns>A copy of this job.</returns>
    public ExportJob Clone() => (ExportJob)MemberwiseClone();
}
