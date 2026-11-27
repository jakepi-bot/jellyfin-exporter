using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.Exporter.Models;

namespace Jellyfin.Plugin.Exporter.Services;

/// <summary>
/// Outcome of turning an expansion into queueable jobs.
/// </summary>
/// <param name="Jobs">Jobs to enqueue, in selection order.</param>
/// <param name="Duplicates">Items skipped because the same item is already queued or running for this profile.</param>
public sealed record ExportPlan(IReadOnlyList<ExportJob> Jobs, IReadOnlyList<MediaTreeNode> Duplicates);

/// <summary>
/// Builds <see cref="ExportJob"/>s for a selection and applies the
/// already-queued guard, without touching Jellyfin or the queue itself.
/// </summary>
public static class ExportSelectionPlanner
{
    /// <summary>
    /// Plans the jobs for one profile.
    /// </summary>
    /// <param name="profile">Profile the operator chose.</param>
    /// <param name="items">Leaf items from <see cref="MediaTreeExpansion"/>.</param>
    /// <param name="existingJobs">Current queue contents, used to skip duplicate work.</param>
    /// <returns>Jobs to enqueue plus the items that were skipped.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="profile"/> is null.</exception>
    public static ExportPlan Plan(
        ExportProfile? profile,
        IEnumerable<MediaTreeNode>? items,
        IEnumerable<ExportJob>? existingJobs)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var unique = new List<MediaTreeNode>();
        var seen = new HashSet<Guid>();
        foreach (var item in items ?? Enumerable.Empty<MediaTreeNode>())
        {
            if (item is not null && seen.Add(item.Id))
            {
                unique.Add(item);
            }
        }

        // A job that has not finished yet for the same item and profile means the
        // operator (or another tab) already asked for this export.
        var inFlight = new HashSet<Guid>();
        foreach (var job in existingJobs ?? Enumerable.Empty<ExportJob>())
        {
            if (job is not null && !job.IsTerminal && job.ProfileId == profile.Id)
            {
                inFlight.Add(job.JellyfinItemId);
            }
        }

        var jobs = new List<ExportJob>();
        var duplicates = new List<MediaTreeNode>();
        foreach (var item in unique)
        {
            if (inFlight.Contains(item.Id))
            {
                duplicates.Add(item);
                continue;
            }

            jobs.Add(CreateJob(profile, item));
        }

        return new ExportPlan(jobs, duplicates);
    }

    /// <summary>
    /// Builds the operator-facing name for a job ("Show S01E02 - Name" for
    /// episodes, the item name otherwise).
    /// </summary>
    /// <param name="item">Item to describe.</param>
    /// <returns>Label used in the status page, logs and notifications.</returns>
    public static string Describe(MediaTreeNode item)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (item.SeasonNumber is not int season || item.EpisodeNumber is not int episode)
        {
            return item.Name;
        }

        var numbers = string.Format(
            CultureInfo.InvariantCulture,
            "S{0:00}E{1:00}",
            season,
            episode);

        return string.IsNullOrWhiteSpace(item.SeriesName)
            ? string.Format(CultureInfo.InvariantCulture, "{0} - {1}", numbers, item.Name)
            : string.Format(CultureInfo.InvariantCulture, "{0} {1} - {2}", item.SeriesName, numbers, item.Name);
    }

    private static ExportJob CreateJob(ExportProfile profile, MediaTreeNode item) => new()
    {
        JellyfinItemId = item.Id,
        ItemName = Describe(item),
        ItemType = item.ItemType,
        ProfileId = profile.Id,
        ProfileName = profile.Name,

        // Snapshot the profile so the job keeps its parameters if the profile
        // is edited or deleted while the job waits in the queue.
        Format = profile.Format,
        Resolution = profile.Resolution,
        AudioChannels = profile.AudioChannels,
        OutputDirectory = profile.OutputDirectory,
        UseJellyfinTranscodingSettings = profile.UseJellyfinTranscodingSettings,
        Status = ExportJobStatus.Queued,
        CreatedAt = DateTimeOffset.UtcNow
    };
}
