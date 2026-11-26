using System;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using Jellyfin.Plugin.Exporter.Configuration;
using Jellyfin.Plugin.Exporter.Models;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Model.Activity;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Exporter.Services;

/// <summary>
/// Delivers the operator-facing message for an export that reached a terminal
/// state.
/// </summary>
public interface IExportNotifier
{
    /// <summary>
    /// Sends the notification for a finished job. Implementations must never
    /// throw: a delivery problem may not fail an export.
    /// </summary>
    /// <param name="job">Job in its terminal state.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when delivery was attempted.</returns>
    Task NotifyAsync(ExportJob job, CancellationToken cancellationToken);
}

/// <summary>
/// Raises Jellyfin's native notification for finished exports.
/// </summary>
/// <remarks>
/// Jellyfin 12 removed the <c>INotificationManager</c> notification subsystem
/// (and the <c>/Notifications</c> endpoints): the server's own user-facing
/// record of an event is now an <see cref="ActivityLog"/> entry, which the
/// dashboard streams live on its Activity page. That is the native notification
/// surface this plugin targets. The notification text is still built exactly as
/// the export UI and the job history present it, and the two notification
/// toggles in the plugin settings gate it (<see cref="PluginConfiguration.NotifyOnCompletion"/>
/// and <see cref="PluginConfiguration.NotifyOnFailure"/>).
/// </remarks>
public sealed class ExportNotificationService : IExportNotifier
{
    /// <summary>Longest FFmpeg error excerpt put into a notification.</summary>
    public const int MaxErrorLength = 200;

    /// <summary>Activity type recorded for a successful export.</summary>
    public const string CompletedType = "ExportCompleted";

    /// <summary>Activity type recorded for a failed export.</summary>
    public const string FailedType = "ExportFailed";

    private readonly IActivityManager? _activityManager;
    private readonly ILogger? _logger;
    private readonly Func<PluginConfiguration?> _configurationProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="ExportNotificationService"/>
    /// class for the server container.
    /// </summary>
    /// <param name="activityManager">Jellyfin's activity log.</param>
    /// <param name="logger">Logger.</param>
    public ExportNotificationService(IActivityManager activityManager, ILogger<ExportNotificationService> logger)
        : this(activityManager, logger, () => Plugin.Instance?.Configuration)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ExportNotificationService"/>
    /// class with an explicit configuration source (tests, and the parameterless
    /// fallback used before the plugin services exist).
    /// </summary>
    /// <param name="activityManager">Jellyfin's activity log, or null to log only.</param>
    /// <param name="logger">Logger, or null in tests.</param>
    /// <param name="configurationProvider">Resolves the notification toggles.</param>
    public ExportNotificationService(
        IActivityManager? activityManager,
        ILogger? logger,
        Func<PluginConfiguration?> configurationProvider)
    {
        _activityManager = activityManager;
        _logger = logger;
        _configurationProvider = configurationProvider ?? (() => null);
    }

    /// <summary>
    /// Builds the notification title for a job.
    /// </summary>
    /// <param name="job">Terminal job.</param>
    /// <returns>"Export completed" or "Export failed".</returns>
    public static string BuildTitle(ExportJob job) =>
        job.Status == ExportJobStatus.Completed ? "Export completed" : "Export failed";

    /// <summary>
    /// Builds the notification body for a job.
    /// </summary>
    /// <param name="job">Terminal job.</param>
    /// <returns>One line naming the item, the profile and the outcome.</returns>
    public static string BuildMessage(ExportJob job)
    {
        var item = string.IsNullOrWhiteSpace(job.ItemName) ? job.JellyfinItemId.ToString("N") : job.ItemName.Trim();
        var profile = string.IsNullOrWhiteSpace(job.ProfileName) ? "no profile" : job.ProfileName.Trim();

        if (job.Status == ExportJobStatus.Completed)
        {
            var path = string.IsNullOrWhiteSpace(job.OutputPath) ? "(no output path reported)" : job.OutputPath;
            return $"Export completed: {item} ({profile}) -> {path}";
        }

        return $"Export failed: {item} ({profile}) — {ShortenError(job.ErrorMessage)}";
    }

    /// <summary>
    /// Reduces an FFmpeg error to the single most useful line.
    /// </summary>
    /// <param name="error">Captured error text.</param>
    /// <returns>A short, non-empty excerpt.</returns>
    public static string ShortenError(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return "no error detail was captured";
        }

        var firstLine = error
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.Length > 0) ?? "no error detail was captured";

        return firstLine.Length <= MaxErrorLength
            ? firstLine
            : firstLine.Substring(0, MaxErrorLength - 3) + "...";
    }

    /// <inheritdoc />
    public async Task NotifyAsync(ExportJob job, CancellationToken cancellationToken)
    {
        try
        {
            if (job is null || !job.IsTerminal)
            {
                return;
            }

            // Cancellations are an operator action, not an outcome worth alerting on.
            if (job.Status == ExportJobStatus.Cancelled)
            {
                return;
            }

            if (!ShouldNotify(job.Status))
            {
                return;
            }

            var title = BuildTitle(job);
            var message = BuildMessage(job);
            _logger?.LogInformation("Export notification: {Message}", message);

            if (_activityManager is null)
            {
                return;
            }

            var entry = new ActivityLog(title, job.Status == ExportJobStatus.Completed ? CompletedType : FailedType, Guid.Empty)
            {
                Overview = message,
                ShortOverview = message,
                ItemId = job.JellyfinItemId == Guid.Empty ? null : job.JellyfinItemId.ToString("N"),
                LogSeverity = job.Status == ExportJobStatus.Completed ? LogLevel.Information : LogLevel.Error,
                DateCreated = DateTime.UtcNow
            };

            await _activityManager.CreateAsync(entry).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Notification delivery must never break an export or the queue.
            _logger?.LogWarning(ex, "Delivering the export notification for job {JobId} failed.", job?.Id);
        }
    }

    private bool ShouldNotify(ExportJobStatus status)
    {
        PluginConfiguration? configuration;
        try
        {
            configuration = _configurationProvider();
        }
        catch (Exception)
        {
            configuration = null;
        }

        // No configuration yet (plugin still loading) means the documented
        // default: notify.
        if (configuration is null)
        {
            return true;
        }

        return status == ExportJobStatus.Completed
            ? configuration.NotifyOnCompletion
            : configuration.NotifyOnFailure;
    }
}

/// <summary>
/// Watches the queue and delivers exactly one notification per job.
/// </summary>
/// <remarks>
/// The queue raises its event on the worker's thread, so the listener only
/// enqueues the finished job and a single background pump does the delivery: a
/// slow activity-log write cannot stall FFmpeg cleanup, and shutdown can drain
/// what is pending. A job id is remembered once it has been notified, so a
/// duplicate event (or a repeated terminal transition) cannot produce a second
/// notification.
/// </remarks>
public sealed class ExportNotificationListener : IHostedService
{
    private static readonly TimeSpan ShutdownDrainTimeout = TimeSpan.FromSeconds(5);

    private readonly IExportQueue _queue;
    private readonly IExportNotifier _notifier;
    private readonly ILogger<ExportNotificationListener>? _logger;
    private readonly ConcurrentDictionary<Guid, ExportJobStatus> _notified = new();
    private readonly Channel<ExportJob> _pending = Channel.CreateUnbounded<ExportJob>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private CancellationTokenSource? _cancellation;
    private Task? _pump;

    /// <summary>
    /// Initializes a new instance of the <see cref="ExportNotificationListener"/> class.
    /// </summary>
    /// <param name="queue">Queue to watch.</param>
    /// <param name="notifier">Delivery.</param>
    /// <param name="logger">Logger.</param>
    public ExportNotificationListener(
        IExportQueue queue,
        IExportNotifier notifier,
        ILogger<ExportNotificationListener> logger)
    {
        _queue = queue;
        _notifier = notifier;
        _logger = logger;
    }

    /// <summary>
    /// Gets how many notifications have been delivered (for diagnostics and tests).
    /// </summary>
    public int DeliveredCount { get; private set; }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cancellation = new CancellationTokenSource();
        _pump = Task.Run(() => PumpAsync(_cancellation.Token), CancellationToken.None);
        _queue.JobStatusChanged += OnJobStatusChanged;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _queue.JobStatusChanged -= OnJobStatusChanged;
        _pending.Writer.TryComplete();

        if (_pump is not null)
        {
            try
            {
                await _pump.WaitAsync(ShutdownDrainTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                _logger?.LogDebug(ex, "Pending export notifications did not drain before shutdown.");
            }
        }

        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _cancellation = null;
        _pump = null;
    }

    private void OnJobStatusChanged(object? sender, ExportJobStatusChangedEventArgs args)
    {
        var job = args?.Job;
        if (job is null || !job.IsTerminal)
        {
            return;
        }

        if (!_notified.TryAdd(job.Id, job.Status))
        {
            return;
        }

        _pending.Writer.TryWrite(job);
    }

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var job in _pending.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    await _notifier.NotifyAsync(job, cancellationToken).ConfigureAwait(false);
                    DeliveredCount++;
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Notifying about job {JobId} failed.", job.Id);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
    }
}
