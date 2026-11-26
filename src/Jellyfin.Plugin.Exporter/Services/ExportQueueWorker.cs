using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Exporter.Configuration;
using Jellyfin.Plugin.Exporter.Export;
using Jellyfin.Plugin.Exporter.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Exporter.Services;

/// <summary>
/// Supplies the live concurrency limit from the plugin configuration.
/// </summary>
public interface IExportSettings
{
    /// <summary>
    /// Gets how many FFmpeg exports may run at the same time (1-8).
    /// </summary>
    int MaxConcurrentExports { get; }
}

/// <summary>
/// Reads the limit from the saved plugin configuration on every poll, so an
/// operator change takes effect without a restart.
/// </summary>
public sealed class PluginExportSettings : IExportSettings
{
    /// <inheritdoc />
    public int MaxConcurrentExports
    {
        get
        {
            var configured = Plugin.Instance?.Configuration?.MaxConcurrentExports ?? 1;
            return Math.Clamp(configured, 1, PluginConfiguration.MaxConcurrentExportsLimit);
        }
    }
}

/// <summary>
/// Drains the export queue: takes queued jobs, runs at most
/// <see cref="IExportSettings.MaxConcurrentExports"/> FFmpeg processes, reports
/// progress, and records the terminal state. Shutdown cancels running exports
/// (FFmpeg is killed and partial files are deleted).
/// </summary>
public sealed class ExportQueueWorker : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ShutdownGracePeriod = TimeSpan.FromSeconds(30);

    private readonly ExportQueue _queue;
    private readonly IExportItemResolver _resolver;
    private readonly IExportEngine _engine;
    private readonly IExportSettings _settings;
    private readonly ILogger<ExportQueueWorker> _logger;
    private readonly object _runningGate = new();
    private readonly List<Task> _running = new();

    public ExportQueueWorker(
        ExportQueue queue,
        IExportItemResolver resolver,
        IExportEngine engine,
        IExportSettings settings,
        ILogger<ExportQueueWorker> logger)
    {
        _queue = queue;
        _resolver = resolver;
        _engine = engine;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>
    /// Gets the number of exports currently in flight (for tests and logging).
    /// </summary>
    public int RunningCount
    {
        get
        {
            lock (_runningGate)
            {
                return _running.Count(task => !task.IsCompleted);
            }
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Jellyfin Exporter queue worker started.");

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    StartQueuedJobs(stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Scanning the export queue failed; retrying on the next poll.");
                }

                try
                {
                    await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        finally
        {
            await DrainAsync().ConfigureAwait(false);
            _logger.LogInformation("Jellyfin Exporter queue worker stopped.");
        }
    }

    private void StartQueuedJobs(CancellationToken stoppingToken)
    {
        List<Task> running;
        lock (_runningGate)
        {
            _running.RemoveAll(task => task.IsCompleted);
            running = _running.ToList();
        }

        var capacity = _settings.MaxConcurrentExports - running.Count;
        if (capacity <= 0)
        {
            return;
        }

        var queued = _queue.GetJobs()
            .Where(job => job.Status == ExportJobStatus.Queued)
            .Take(capacity)
            .ToList();

        foreach (var candidate in queued)
        {
            if (!_queue.TryBegin(candidate.Id, out var job, out var cancellationToken))
            {
                continue;
            }

            var task = Task.Run(
                () => RunJobAsync(job, cancellationToken, stoppingToken),
                CancellationToken.None);

            lock (_runningGate)
            {
                _running.Add(task);
            }
        }
    }

    private async Task RunJobAsync(ExportJob job, CancellationToken queueToken, CancellationToken stoppingToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(queueToken, stoppingToken);
        var jobId = job.Id;

        try
        {
            _logger.LogInformation(
                "Export {JobId} started: '{Item}' with profile '{Profile}' ({Format}, {Resolution}, {Audio}).",
                jobId,
                job.ItemName,
                job.ProfileName,
                job.Format,
                job.Resolution,
                job.AudioChannels);

            var item = await _resolver.ResolveAsync(job, linked.Token).ConfigureAwait(false);
            var result = await _engine
                .RunAsync(job, item, percent => _queue.ReportProgress(jobId, percent), linked.Token)
                .ConfigureAwait(false);

            switch (result.Outcome)
            {
                case ExportJobStatus.Completed:
                    _queue.Complete(jobId, result.OutputPath ?? string.Empty);
                    _logger.LogInformation("Export {JobId} completed: {OutputPath}", jobId, result.OutputPath);
                    break;
                case ExportJobStatus.Cancelled:
                    _queue.MarkCancelled(jobId, result.ErrorMessage ?? "The export was cancelled.");
                    _logger.LogInformation("Export {JobId} cancelled.", jobId);
                    break;
                default:
                    _queue.Fail(jobId, result.ErrorMessage ?? "The export failed.");
                    _logger.LogWarning("Export {JobId} failed: {Error}", jobId, result.ErrorMessage);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            _queue.MarkCancelled(jobId, "The export was cancelled.");
            _logger.LogInformation("Export {JobId} cancelled.", jobId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Export {JobId} failed unexpectedly.", jobId);
            _queue.Fail(jobId, ex.Message);
        }
    }

    private async Task DrainAsync()
    {
        Task[] snapshot;
        lock (_runningGate)
        {
            snapshot = _running.ToArray();
        }

        if (snapshot.Length == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(snapshot).WaitAsync(ShutdownGracePeriod).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Running exports did not stop within {Seconds}s of shutdown.", ShutdownGracePeriod.TotalSeconds);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "A running export ended with an error during shutdown.");
        }
    }
}
