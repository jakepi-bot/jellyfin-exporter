using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Jellyfin.Plugin.Exporter.Models;

namespace Jellyfin.Plugin.Exporter.Services;

/// <summary>
/// In-memory job queue behind <see cref="IExportQueue"/>: the queue itself, the
/// cancellation tokens of running jobs, and the retention window the status
/// page reads. Thread safe; subscribers get copies and cannot break the queue.
/// </summary>
/// <remarks>
/// The worker-facing methods (<see cref="TryBegin"/>, <see cref="ReportProgress"/>,
/// <see cref="Complete"/>, <see cref="Fail"/>, <see cref="MarkCancelled"/>) are
/// the only mutation path, so persistence and status events see exactly one
/// change per transition. When a store is supplied, a snapshot of the job list
/// is written after every transition and restored on construction.
/// </remarks>
public class ExportQueue : IExportQueue
{
    /// <summary>Default number of finished jobs kept for the status page.</summary>
    public const int DefaultRetentionLimit = 200;

    /// <summary>Highest history limit accepted, so a bad setting cannot grow the file without bound.</summary>
    public const int MaxRetentionLimit = 5000;

    /// <summary>Error recorded for a job that was still running when the server stopped.</summary>
    public const string InterruptedMessage =
        "The Jellyfin server restarted while this export was running, so the export was interrupted. Queue it again to finish it.";

    private readonly object _gate = new();
    private readonly object _persistGate = new();
    private readonly List<ExportJob> _jobs = new();
    private readonly Dictionary<Guid, CancellationTokenSource> _cancellations = new();
    private readonly Func<int> _retentionLimitProvider;
    private readonly IExportJobStore? _store;
    private long _version;
    private long _persistedVersion;

    /// <summary>
    /// Creates the queue with the default retention window and no persistence.
    /// Present as its own constructor because the server's dependency-injection
    /// container resolves only parameterless constructors.
    /// </summary>
    public ExportQueue()
        : this(() => DefaultRetentionLimit, null)
    {
    }

    /// <summary>
    /// Creates the queue with a fixed retention window and no persistence.
    /// </summary>
    /// <param name="retentionLimit">How many finished jobs to keep; at least 1.</param>
    public ExportQueue(int retentionLimit)
        : this(() => retentionLimit, null)
    {
    }

    /// <summary>
    /// Creates the queue, restoring whatever the store already holds.
    /// </summary>
    /// <param name="retentionLimitProvider">
    /// Resolves the retention window on every prune, so a settings change takes
    /// effect without a restart.
    /// </param>
    /// <param name="store">Durable history, or null for an in-memory queue.</param>
    public ExportQueue(Func<int> retentionLimitProvider, IExportJobStore? store)
    {
        _retentionLimitProvider = retentionLimitProvider ?? (() => DefaultRetentionLimit);
        _store = store;
        Restore();
    }

    /// <summary>
    /// Gets the number of finished jobs that are kept for the status page.
    /// </summary>
    public int RetentionLimit
    {
        get
        {
            int configured;
            try
            {
                configured = _retentionLimitProvider();
            }
            catch (Exception)
            {
                return DefaultRetentionLimit;
            }

            return Math.Clamp(configured, 1, MaxRetentionLimit);
        }
    }

    /// <summary>
    /// Gets the path the job history is stored at, or null for an in-memory queue.
    /// </summary>
    public string? StorageLocation => _store?.Location;

    /// <summary>
    /// Gets the last persistence problem, or null when the history is healthy.
    /// </summary>
    public string? StorageError => _store?.LastError;

    /// <inheritdoc />
    public event EventHandler<ExportJobStatusChangedEventArgs>? JobStatusChanged;

    /// <inheritdoc />
    public IReadOnlyList<ExportJob> Enqueue(IEnumerable<ExportJob> jobs)
    {
        ArgumentNullException.ThrowIfNull(jobs);

        var added = new List<ExportJob>();
        var transitions = new List<(ExportJob Job, ExportJobStatus Previous)>();
        lock (_gate)
        {
            foreach (var job in jobs)
            {
                if (job is null)
                {
                    continue;
                }

                if (job.Id == Guid.Empty)
                {
                    job.Id = Guid.NewGuid();
                }

                if (_jobs.Any(existing => existing.Id == job.Id))
                {
                    continue;
                }

                job.Status = ExportJobStatus.Queued;
                job.ProgressPercent = 0;
                job.OutputPath = null;
                job.ErrorMessage = null;
                job.StartedAt = null;
                job.FinishedAt = null;
                if (job.CreatedAt == default)
                {
                    job.CreatedAt = DateTimeOffset.UtcNow;
                }

                _jobs.Add(job);
                added.Add(job.Clone());
                transitions.Add((job, ExportJobStatus.Queued));
            }

            PruneLocked();
            if (added.Count > 0)
            {
                _version++;
            }
        }

        foreach (var (job, previous) in transitions)
        {
            Raise(job, previous);
        }

        if (transitions.Count > 0)
        {
            Persist();
        }

        return added;
    }

    /// <inheritdoc />
    public IReadOnlyList<ExportJob> GetJobs()
    {
        lock (_gate)
        {
            return _jobs
                .OrderBy(job => job.CreatedAt)
                .Select(job => job.Clone())
                .ToList();
        }
    }

    /// <inheritdoc />
    public bool Cancel(Guid jobId)
    {
        ExportJob? target = null;
        var previous = ExportJobStatus.Queued;
        lock (_gate)
        {
            var job = Find(jobId);
            if (job is null || job.IsTerminal)
            {
                return false;
            }

            previous = job.Status;
            target = job;
            if (job.Status == ExportJobStatus.Queued)
            {
                job.Status = ExportJobStatus.Cancelled;
                job.FinishedAt = DateTimeOffset.UtcNow;
                job.ErrorMessage = "Cancelled before the export started.";
                _version++;
            }
            else if (!_cancellations.TryGetValue(jobId, out var cancellation))
            {
                return false;
            }
            else
            {
                // The worker observes the token, kills FFmpeg, deletes the
                // partial file and marks the job Cancelled.
                cancellation.Cancel();
                return true;
            }
        }

        Raise(target, previous);
        Persist();
        return true;
    }

    /// <summary>
    /// Claims a queued job for a worker slot: Queued becomes Running and a
    /// cancellation token is issued for it.
    /// </summary>
    /// <param name="jobId">Job id.</param>
    /// <param name="job">Copy of the job in its Running state.</param>
    /// <param name="cancellationToken">Token cancelled by <see cref="Cancel"/>.</param>
    /// <returns>True when the job was claimed.</returns>
    public bool TryBegin(Guid jobId, out ExportJob job, out CancellationToken cancellationToken)
    {
        ExportJob? claimed = null;
        CancellationTokenSource? source = null;
        lock (_gate)
        {
            var found = Find(jobId);
            if (found is null || found.Status != ExportJobStatus.Queued)
            {
                job = null!;
                cancellationToken = default;
                return false;
            }

            found.Status = ExportJobStatus.Running;
            found.StartedAt = DateTimeOffset.UtcNow;
            found.ProgressPercent = 0;
            found.ErrorMessage = null;
            source = new CancellationTokenSource();
            _cancellations[jobId] = source;
            claimed = found;
            job = found.Clone();
            cancellationToken = source.Token;
            _version++;
        }

        Raise(claimed, ExportJobStatus.Queued);
        Persist();
        return true;
    }

    /// <summary>
    /// Records progress for a running job. Progress is not a status change, so
    /// no event is raised; readers poll <see cref="GetJobs"/>.
    /// </summary>
    /// <param name="jobId">Job id.</param>
    /// <param name="percent">Percentage 0-100.</param>
    public void ReportProgress(Guid jobId, double percent)
    {
        if (double.IsNaN(percent) || double.IsInfinity(percent))
        {
            return;
        }

        lock (_gate)
        {
            var job = Find(jobId);
            if (job is null || job.Status != ExportJobStatus.Running)
            {
                return;
            }

            job.ProgressPercent = Math.Clamp(percent, 0d, 100d);
        }
    }

    /// <summary>
    /// Marks a job completed with its output path.
    /// </summary>
    /// <param name="jobId">Job id.</param>
    /// <param name="outputPath">Final file path.</param>
    public void Complete(Guid jobId, string outputPath) =>
        Transition(jobId, job =>
        {
            job.Status = ExportJobStatus.Completed;
            job.ProgressPercent = 100;
            job.OutputPath = outputPath;
            job.ErrorMessage = null;
            job.FinishedAt = DateTimeOffset.UtcNow;
        });

    /// <summary>
    /// Marks a job failed with the captured error.
    /// </summary>
    /// <param name="jobId">Job id.</param>
    /// <param name="errorMessage">Operator-facing error.</param>
    public void Fail(Guid jobId, string errorMessage) =>
        Transition(jobId, job =>
        {
            job.Status = ExportJobStatus.Failed;
            job.ErrorMessage = errorMessage;
            job.FinishedAt = DateTimeOffset.UtcNow;
        });

    /// <summary>
    /// Marks a job cancelled.
    /// </summary>
    /// <param name="jobId">Job id.</param>
    /// <param name="reason">Optional reason stored as the error message.</param>
    public void MarkCancelled(Guid jobId, string? reason = null) =>
        Transition(jobId, job =>
        {
            job.Status = ExportJobStatus.Cancelled;
            job.ErrorMessage = reason;
            job.FinishedAt = DateTimeOffset.UtcNow;
        });

    private void Transition(Guid jobId, Action<ExportJob> mutation)
    {
        ExportJob? target = null;
        var previous = ExportJobStatus.Queued;
        lock (_gate)
        {
            var job = Find(jobId);
            if (job is null || job.IsTerminal)
            {
                return;
            }

            previous = job.Status;
            mutation(job);
            if (_cancellations.Remove(jobId, out var cancellation))
            {
                cancellation.Dispose();
            }

            target = job;
            PruneLocked();
            _version++;
        }

        Raise(target, previous);
        Persist();
    }

    private ExportJob? Find(Guid jobId) => _jobs.FirstOrDefault(job => job.Id == jobId);

    private void PruneLocked()
    {
        var limit = RetentionLimit;
        var terminal = _jobs.Where(job => job.IsTerminal).ToList();
        if (terminal.Count <= limit)
        {
            return;
        }

        // Queued and running jobs are never pruned: the retention limit only
        // bounds the history the status page shows.
        var removable = terminal
            .OrderBy(job => job.FinishedAt ?? job.CreatedAt)
            .Take(terminal.Count - limit)
            .ToList();

        foreach (var job in removable)
        {
            _jobs.Remove(job);
        }
    }

    /// <summary>
    /// Reloads the job history from the store. A job that was running when the
    /// server stopped cannot be resumed (its FFmpeg process is gone and the
    /// partial file was abandoned), so it is recorded as failed with an
    /// explanation instead of sitting in the running state forever. Queued jobs
    /// stay queued and the worker picks them up again.
    /// </summary>
    /// <remarks>
    /// No status events are raised while restoring: notifications for exports
    /// that were interrupted before the restart are not delivered after it, so a
    /// history cannot produce a burst of alerts on boot.
    /// </remarks>
    private void Restore()
    {
        if (_store is null)
        {
            return;
        }

        IReadOnlyList<ExportJob> loaded;
        try
        {
            loaded = _store.Load();
        }
        catch (Exception)
        {
            return;
        }

        if (loaded.Count == 0)
        {
            return;
        }

        lock (_gate)
        {
            foreach (var job in loaded)
            {
                if (job is null || job.Id == Guid.Empty || _jobs.Any(existing => existing.Id == job.Id))
                {
                    continue;
                }

                if (job.CreatedAt == default)
                {
                    job.CreatedAt = DateTimeOffset.UtcNow;
                }

                if (job.Status == ExportJobStatus.Running)
                {
                    job.Status = ExportJobStatus.Failed;
                    job.ErrorMessage = InterruptedMessage;
                    job.FinishedAt = DateTimeOffset.UtcNow;
                }
                else if (job.Status == ExportJobStatus.Queued)
                {
                    job.ProgressPercent = 0;
                }
                else if (job.Status == ExportJobStatus.Completed && job.ProgressPercent < 100)
                {
                    job.ProgressPercent = 100;
                }

                _jobs.Add(job);
            }

            PruneLocked();
            _version++;
        }

        Persist();
    }

    /// <summary>
    /// Writes the current job list through the store. Called after every
    /// transition, outside the queue lock. A write that was overtaken by a newer
    /// transition is skipped so an older snapshot can never win.
    /// </summary>
    private void Persist()
    {
        if (_store is null)
        {
            return;
        }

        List<ExportJob> snapshot;
        long version;
        lock (_gate)
        {
            snapshot = _jobs.Select(job => job.Clone()).ToList();
            version = _version;
        }

        lock (_persistGate)
        {
            if (version <= _persistedVersion)
            {
                return;
            }

            _store.Save(snapshot);
            if (_store.LastError is null)
            {
                _persistedVersion = version;
            }
        }
    }

    private void Raise(ExportJob job, ExportJobStatus previousStatus)
    {
        var handlers = JobStatusChanged;
        if (handlers is null)
        {
            return;
        }

        var args = new ExportJobStatusChangedEventArgs(job.Clone(), previousStatus);
        foreach (var handler in handlers.GetInvocationList().Cast<EventHandler<ExportJobStatusChangedEventArgs>>())
        {
            try
            {
                handler(this, args);
            }
            catch (Exception)
            {
                // A subscriber must never break the queue.
            }
        }
    }
}
