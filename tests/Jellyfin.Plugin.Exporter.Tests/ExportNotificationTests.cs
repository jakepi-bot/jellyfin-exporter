using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Data.Events;
using Jellyfin.Data.Queries;
using Jellyfin.Plugin.Exporter.Configuration;
using Jellyfin.Plugin.Exporter.Models;
using Jellyfin.Plugin.Exporter.Services;
using MediaBrowser.Model.Activity;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging.Abstractions;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace Jellyfin.Plugin.Exporter.Tests;

public class ExportNotificationTests
{
    private static ExportJob Finished(
        ExportJobStatus status,
        string itemName = "The Movie",
        string profile = "Archive 1080p",
        string? outputPath = "/exports/Movies/The Movie (2020)/The Movie (2020).mkv",
        string? error = null) => new()
    {
        ItemName = itemName,
        ProfileName = profile,
        Status = status,
        ProgressPercent = status == ExportJobStatus.Completed ? 100 : 0,
        OutputPath = outputPath,
        ErrorMessage = error
    };

    private static PluginConfiguration Config(bool notifyOnCompletion = true, bool notifyOnFailure = true) => new()
    {
        NotifyOnCompletion = notifyOnCompletion,
        NotifyOnFailure = notifyOnFailure
    };

    private static ExportNotificationService CreateService(
        FakeActivityManager activity,
        PluginConfiguration? configuration = null) =>
        new(activity, NullLogger<ExportNotificationService>.Instance, () => configuration ?? Config());

    [Fact]
    public void BuildMessage_NamesTheItemProfileAndOutputPath()
    {
        var message = ExportNotificationService.BuildMessage(Finished(ExportJobStatus.Completed));

        Assert.Equal(
            "Export completed: The Movie (Archive 1080p) -> /exports/Movies/The Movie (2020)/The Movie (2020).mkv",
            message);
    }

    [Fact]
    public void BuildMessage_FallsBackWhenTheOutputPathIsMissing()
    {
        var message = ExportNotificationService.BuildMessage(Finished(ExportJobStatus.Completed, outputPath: null));

        Assert.Contains("(no output path reported)", message);
    }

    [Fact]
    public void BuildMessage_UsesTheFirstErrorLineForAFailure()
    {
        var message = ExportNotificationService.BuildMessage(Finished(
            ExportJobStatus.Failed,
            error: "FFmpeg exited with code 1: Unknown encoder 'h264_nvenc'\n  second line of noise"));

        Assert.Equal(
            "Export failed: The Movie (Archive 1080p) — FFmpeg exited with code 1: Unknown encoder 'h264_nvenc'",
            message);
    }

    [Fact]
    public void ShortenError_TruncatesAVeryLongLine()
    {
        var shortened = ExportNotificationService.ShortenError(new string('x', 500));

        Assert.Equal(ExportNotificationService.MaxErrorLength, shortened.Length);
        Assert.EndsWith("...", shortened);
    }

    [Fact]
    public void ShortenError_SurvivesAnEmptyError()
    {
        Assert.Equal("no error detail was captured", ExportNotificationService.ShortenError(null));
        Assert.Equal("no error detail was captured", ExportNotificationService.ShortenError("  \n\n "));
    }

    [Fact]
    public async Task Notify_RecordsOneActivityEntryForACompletion()
    {
        var activity = new FakeActivityManager();
        await CreateService(activity).NotifyAsync(Finished(ExportJobStatus.Completed), CancellationToken.None);

        var entry = Assert.Single(activity.Entries);
        Assert.Equal("Export completed", entry.Name);
        Assert.Equal(ExportNotificationService.CompletedType, entry.Type);
        Assert.Equal(LogLevel.Information, entry.LogSeverity);
        Assert.Equal(
            "Export completed: The Movie (Archive 1080p) -> /exports/Movies/The Movie (2020)/The Movie (2020).mkv",
            entry.Overview);
        Assert.Equal(Guid.Empty, entry.UserId);
    }

    [Fact]
    public async Task Notify_RecordsAFailureAsAnError()
    {
        var activity = new FakeActivityManager();
        await CreateService(activity).NotifyAsync(
            Finished(ExportJobStatus.Failed, error: "FFmpeg exited with code 1: boom"),
            CancellationToken.None);

        var entry = Assert.Single(activity.Entries);
        Assert.Equal("Export failed", entry.Name);
        Assert.Equal(ExportNotificationService.FailedType, entry.Type);
        Assert.Equal(LogLevel.Error, entry.LogSeverity);
        Assert.Equal("Export failed: The Movie (Archive 1080p) — FFmpeg exited with code 1: boom", entry.Overview);
    }

    [Fact]
    public async Task Notify_RespectsTheCompletionToggle()
    {
        var activity = new FakeActivityManager();
        var service = CreateService(activity, Config(notifyOnCompletion: false));

        await service.NotifyAsync(Finished(ExportJobStatus.Completed), CancellationToken.None);
        await service.NotifyAsync(Finished(ExportJobStatus.Failed, error: "boom"), CancellationToken.None);

        var entry = Assert.Single(activity.Entries);
        Assert.Equal(ExportNotificationService.FailedType, entry.Type);
    }

    [Fact]
    public async Task Notify_RespectsTheFailureToggle()
    {
        var activity = new FakeActivityManager();
        var service = CreateService(activity, Config(notifyOnFailure: false));

        await service.NotifyAsync(Finished(ExportJobStatus.Completed), CancellationToken.None);
        await service.NotifyAsync(Finished(ExportJobStatus.Failed, error: "boom"), CancellationToken.None);

        var entry = Assert.Single(activity.Entries);
        Assert.Equal(ExportNotificationService.CompletedType, entry.Type);
    }

    [Fact]
    public async Task Notify_SkipsCancelledJobsAndJobsThatAreNotTerminal()
    {
        var activity = new FakeActivityManager();
        var service = CreateService(activity);

        await service.NotifyAsync(Finished(ExportJobStatus.Cancelled), CancellationToken.None);
        await service.NotifyAsync(Finished(ExportJobStatus.Running), CancellationToken.None);
        await service.NotifyAsync(Finished(ExportJobStatus.Queued), CancellationToken.None);

        Assert.Empty(activity.Entries);
    }

    [Fact]
    public async Task Notify_DoesNotThrowWhenTheActivityLogFails()
    {
        var activity = new FakeActivityManager { ThrowOnCreate = true };
        var service = CreateService(activity);

        await service.NotifyAsync(Finished(ExportJobStatus.Completed), CancellationToken.None);
    }

    [Fact]
    public async Task Notify_WorksWithoutAnActivityManager()
    {
        var service = new ExportNotificationService(
            null,
            NullLogger<ExportNotificationService>.Instance,
            () => Config());

        await service.NotifyAsync(Finished(ExportJobStatus.Completed), CancellationToken.None);
    }

    [Fact]
    public async Task Listener_SendsExactlyOneNotificationPerFinishedJob()
    {
        var queue = new FakeQueue();
        var notifier = new CountingNotifier();
        var listener = new ExportNotificationListener(queue, notifier, NullLogger<ExportNotificationListener>.Instance);

        await listener.StartAsync(CancellationToken.None);
        try
        {
            var job = Finished(ExportJobStatus.Completed);

            // The queue itself raises a transition once, but a duplicate event
            // must not produce a second notification.
            queue.Raise(job, ExportJobStatus.Running);
            queue.Raise(job, ExportJobStatus.Running);

            await notifier.WaitForCountAsync(1);
            await Task.Delay(150);

            Assert.Equal(1, notifier.Count);
            Assert.Equal(1, listener.DeliveredCount);
        }
        finally
        {
            await listener.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Listener_IgnoresQueuedAndRunningTransitions()
    {
        var queue = new FakeQueue();
        var notifier = new CountingNotifier();
        var listener = new ExportNotificationListener(queue, notifier, NullLogger<ExportNotificationListener>.Instance);

        await listener.StartAsync(CancellationToken.None);
        try
        {
            queue.Raise(Finished(ExportJobStatus.Queued), ExportJobStatus.Completed);
            queue.Raise(Finished(ExportJobStatus.Running), ExportJobStatus.Queued);
            queue.Raise(Finished(ExportJobStatus.Failed, error: "boom"), ExportJobStatus.Running);

            await notifier.WaitForCountAsync(1);
            await Task.Delay(100);

            Assert.Equal(1, notifier.Count);
            Assert.Equal(ExportJobStatus.Failed, notifier.Received[0].Status);
        }
        finally
        {
            await listener.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Listener_KeepsWorkingAfterANotifierFailure()
    {
        var queue = new FakeQueue();
        var notifier = new CountingNotifier { FailFirst = true };
        var listener = new ExportNotificationListener(queue, notifier, NullLogger<ExportNotificationListener>.Instance);

        await listener.StartAsync(CancellationToken.None);
        try
        {
            queue.Raise(Finished(ExportJobStatus.Completed, itemName: "First"), ExportJobStatus.Running);
            await notifier.WaitForCountAsync(1);
            queue.Raise(Finished(ExportJobStatus.Completed, itemName: "Second"), ExportJobStatus.Running);
            await notifier.WaitForCountAsync(2);

            Assert.Equal(2, notifier.Count);
        }
        finally
        {
            await listener.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task QueueWithListener_NotifiesOnceWhenTheWorkerFinishesAJob()
    {
        var store = new RecordingActivityOnlyNotifier();
        var queue = new ExportQueue();
        var listener = new ExportNotificationListener(queue, store, NullLogger<ExportNotificationListener>.Instance);

        await listener.StartAsync(CancellationToken.None);
        try
        {
            var job = queue.Enqueue(new[] { Finished(ExportJobStatus.Queued) })[0];
            queue.TryBegin(job.Id, out _, out _);
            queue.Complete(job.Id, "/exports/out.mkv");

            // A second terminal transition is impossible through the queue API,
            // and the listener would ignore it anyway.
            queue.Complete(job.Id, "/exports/out.mkv");

            await store.WaitForCountAsync(1);
            await Task.Delay(150);

            Assert.Equal(1, store.Count);
            Assert.Equal(ExportJobStatus.Completed, store.Received[0].Status);
        }
        finally
        {
            await listener.StopAsync(CancellationToken.None);
        }
    }

    private sealed class FakeActivityManager : IActivityManager
    {
        private readonly object _gate = new();

        public event EventHandler<GenericEventArgs<ActivityLogEntry>>? EntryCreated
        {
            add { }
            remove { }
        }

        public List<ActivityLog> Entries { get; } = new();

        public bool ThrowOnCreate { get; init; }

        public Task CreateAsync(ActivityLog entry)
        {
            if (ThrowOnCreate)
            {
                throw new InvalidOperationException("the activity database is unavailable");
            }

            lock (_gate)
            {
                Entries.Add(entry);
            }

            return Task.CompletedTask;
        }

        public Task<QueryResult<ActivityLogEntry>> GetPagedResultAsync(ActivityLogQuery query) =>
            Task.FromResult(new QueryResult<ActivityLogEntry>());

        public Task CleanAsync(DateTime startDate) => Task.CompletedTask;
    }

    private abstract class RecordingNotifier : IExportNotifier
    {
        private readonly List<ExportJob> _received = new();

        public int Count
        {
            get
            {
                lock (_received)
                {
                    return _received.Count;
                }
            }
        }

        public IReadOnlyList<ExportJob> Received
        {
            get
            {
                lock (_received)
                {
                    return _received.ToList();
                }
            }
        }

        public bool FailFirst { get; init; }

        public abstract Task NotifyAsync(ExportJob job, CancellationToken cancellationToken);

        protected void Record(ExportJob job)
        {
            lock (_received)
            {
                if (FailFirst && _received.Count == 0)
                {
                    _received.Add(job);
                    throw new InvalidOperationException("delivery failed");
                }

                _received.Add(job);
            }
        }

        public Task WaitForCountAsync(int count, int timeoutMs = 5000) => Task.Run(async () =>
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                if (Count >= count)
                {
                    return;
                }

                await Task.Delay(20);
            }

            throw new TimeoutException($"Only {Count} of {count} notifications were delivered.");
        });
    }

    private sealed class CountingNotifier : RecordingNotifier
    {
        public override Task NotifyAsync(ExportJob job, CancellationToken cancellationToken)
        {
            Record(job);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingActivityOnlyNotifier : RecordingNotifier
    {
        public override Task NotifyAsync(ExportJob job, CancellationToken cancellationToken)
        {
            Record(job);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeQueue : IExportQueue
    {
        public event EventHandler<ExportJobStatusChangedEventArgs>? JobStatusChanged;

        public IReadOnlyList<ExportJob> Enqueue(IEnumerable<ExportJob> jobs) => jobs.ToList();

        public IReadOnlyList<ExportJob> GetJobs() => Array.Empty<ExportJob>();

        public bool Cancel(Guid jobId) => false;

        public void Raise(ExportJob job, ExportJobStatus previous)
        {
            JobStatusChanged?.Invoke(this, new ExportJobStatusChangedEventArgs(job, previous));
        }
    }
}
