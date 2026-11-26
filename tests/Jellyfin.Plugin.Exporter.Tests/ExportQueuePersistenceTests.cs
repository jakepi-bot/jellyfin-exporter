using Jellyfin.Plugin.Exporter.Models;
using Jellyfin.Plugin.Exporter.Services;

namespace Jellyfin.Plugin.Exporter.Tests;

public class ExportQueuePersistenceTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "jfx-queue-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }
    }

    private JsonExportJobStore Store() => new(_directory);

    private ExportQueue NewQueue(int retention = 200) => new(() => retention, Store());

    private static ExportJob NewJob(string name = "The Movie", Guid? itemId = null) => new()
    {
        JellyfinItemId = itemId ?? Guid.NewGuid(),
        ItemName = name,
        ItemType = "Movie",
        ProfileName = "Default",
        OutputDirectory = "/exports"
    };

    [Fact]
    public void Enqueue_WritesTheJobToTheHistoryFile()
    {
        var queue = NewQueue();
        var job = queue.Enqueue(new[] { NewJob() })[0];

        Assert.NotNull(queue.StorageLocation);
        Assert.True(File.Exists(queue.StorageLocation));

        var reloaded = NewQueue().GetJobs();
        var stored = Assert.Single(reloaded);
        Assert.Equal(job.Id, stored.Id);
        Assert.Equal(ExportJobStatus.Queued, stored.Status);
        Assert.Equal("The Movie", stored.ItemName);
        Assert.Null(queue.StorageError);
    }

    [Fact]
    public void TerminalStates_SurviveARestart()
    {
        var queue = NewQueue();
        var completed = queue.Enqueue(new[] { NewJob("Finished") })[0];
        var failed = queue.Enqueue(new[] { NewJob("Broken") })[0];

        queue.TryBegin(completed.Id, out _, out _);
        queue.Complete(completed.Id, "/exports/Movies/Finished/Finished.mkv");
        queue.TryBegin(failed.Id, out _, out _);
        queue.Fail(failed.Id, "FFmpeg exited with code 1: boom");

        // A new process reads the same folder.
        var restarted = NewQueue().GetJobs();

        var completedAfter = restarted.Single(job => job.Id == completed.Id);
        Assert.Equal(ExportJobStatus.Completed, completedAfter.Status);
        Assert.Equal("/exports/Movies/Finished/Finished.mkv", completedAfter.OutputPath);
        Assert.Equal(100, completedAfter.ProgressPercent);
        Assert.NotNull(completedAfter.FinishedAt);

        var failedAfter = restarted.Single(job => job.Id == failed.Id);
        Assert.Equal(ExportJobStatus.Failed, failedAfter.Status);
        Assert.Equal("FFmpeg exited with code 1: boom", failedAfter.ErrorMessage);
        Assert.NotNull(failedAfter.FinishedAt);
    }

    [Fact]
    public void AJobThatWasRunningAtShutdown_ComesBackFailedInsteadOfStuckRunning()
    {
        var queue = NewQueue();
        var job = queue.Enqueue(new[] { NewJob() })[0];
        Assert.True(queue.TryBegin(job.Id, out _, out _));

        var restarted = NewQueue().GetJobs().Single();

        Assert.Equal(ExportJobStatus.Failed, restarted.Status);
        Assert.Equal(ExportQueue.InterruptedMessage, restarted.ErrorMessage);
        Assert.NotNull(restarted.FinishedAt);
    }

    [Fact]
    public void AJobThatWasQueuedAtShutdown_IsStillQueuedSoTheWorkerRunsIt()
    {
        NewQueue().Enqueue(new[] { NewJob() });

        var restarted = NewQueue().GetJobs().Single();

        Assert.Equal(ExportJobStatus.Queued, restarted.Status);
        Assert.Equal(0, restarted.ProgressPercent);
        Assert.Null(restarted.StartedAt);
    }

    [Fact]
    public void RestoringRaisesNoEventsSoARestartCannotReplayNotifications()
    {
        var queue = NewQueue();
        var job = queue.Enqueue(new[] { NewJob() })[0];
        queue.TryBegin(job.Id, out _, out _);

        var events = 0;
        var restarted = NewQueue();
        restarted.JobStatusChanged += (_, _) => events++;

        Assert.Single(restarted.GetJobs());
        Assert.Equal(0, events);
    }

    [Fact]
    public void RestoringKeepsTheConfiguredRetentionWindow()
    {
        var store = Store();
        var jobs = Enumerable.Range(0, 10)
            .Select(index => new ExportJob
            {
                ItemName = "Job " + index,
                Status = ExportJobStatus.Completed,
                ProgressPercent = 100,
                OutputPath = "/exports/" + index + ".mkv",
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-10 + index),
                FinishedAt = DateTimeOffset.UtcNow.AddMinutes(-10 + index)
            })
            .ToList();

        store.Save(jobs);

        var queue = new ExportQueue(() => 3, store);

        Assert.Equal(3, queue.GetJobs().Count);
        Assert.Equal(3, queue.RetentionLimit);
        // The newest three are the ones kept.
        Assert.Equal(new[] { "Job 7", "Job 8", "Job 9" }, queue.GetJobs().Select(job => job.ItemName));
    }

    [Fact]
    public void Retention_IsPrunedToTheConfiguredLimitAsJobsFinish()
    {
        var queue = NewQueue(retention: 2);

        for (var index = 0; index < 5; index++)
        {
            var job = queue.Enqueue(new[] { NewJob("Job " + index) })[0];
            queue.TryBegin(job.Id, out _, out _);
            queue.Complete(job.Id, "/exports/" + index + ".mkv");
        }

        Assert.Equal(2, queue.GetJobs().Count);
        Assert.Equal(new[] { "Job 3", "Job 4" }, queue.GetJobs().Select(job => job.ItemName));
        Assert.Equal(2, NewQueue(retention: 2).GetJobs().Count);
    }

    [Fact]
    public void Retention_ReadsTheLimitOnEveryPruneSoASettingsChangeApplies()
    {
        var limit = 5;
        var queue = new ExportQueue(() => limit, Store());

        for (var index = 0; index < 3; index++)
        {
            var job = queue.Enqueue(new[] { NewJob("Job " + index) })[0];
            queue.TryBegin(job.Id, out _, out _);
            queue.Complete(job.Id, "/exports/" + index + ".mkv");
        }

        Assert.Equal(3, queue.GetJobs().Count);

        limit = 1;
        var last = queue.Enqueue(new[] { NewJob("Job last") })[0];
        queue.TryBegin(last.Id, out _, out _);
        queue.Complete(last.Id, "/exports/last.mkv");

        Assert.Single(queue.GetJobs());
        Assert.Equal(1, queue.RetentionLimit);
    }

    [Fact]
    public void Retention_IsClampedToAUsableWindow()
    {
        Assert.Equal(1, new ExportQueue(() => 0, null).RetentionLimit);
        Assert.Equal(1, new ExportQueue(() => -40, null).RetentionLimit);
        Assert.Equal(ExportQueue.MaxRetentionLimit, new ExportQueue(() => 100000, null).RetentionLimit);
        Assert.Equal(ExportQueue.DefaultRetentionLimit, new ExportQueue(() => throw new InvalidOperationException("no config"), null).RetentionLimit);
    }

    [Fact]
    public void QueuedJobs_AreNeverPrunedByTheHistoryLimit()
    {
        var queue = NewQueue(retention: 1);
        queue.Enqueue(new[] { NewJob("One"), NewJob("Two"), NewJob("Three") });

        Assert.Equal(3, queue.GetJobs().Count);
        Assert.Equal(3, NewQueue(retention: 1).GetJobs().Count);
    }

    [Fact]
    public void ProgressIsNotPersistedButTheFinalStateIs()
    {
        var queue = NewQueue();
        var job = queue.Enqueue(new[] { NewJob() })[0];
        queue.TryBegin(job.Id, out _, out _);
        queue.ReportProgress(job.Id, 55.5);

        Assert.Equal(0, NewQueue().GetJobs().Single().ProgressPercent);

        queue.Complete(job.Id, "/exports/out.mkv");
        Assert.Equal(100, NewQueue().GetJobs().Single().ProgressPercent);
    }

    [Fact]
    public void AStoreFailureDoesNotBreakTheQueue()
    {
        var queue = new ExportQueue(() => 200, new ExplodingStore());

        var job = queue.Enqueue(new[] { NewJob() })[0];
        queue.TryBegin(job.Id, out _, out _);
        queue.Complete(job.Id, "/exports/out.mkv");

        var stored = queue.GetJobs().Single();
        Assert.Equal(ExportJobStatus.Completed, stored.Status);
        Assert.NotNull(queue.StorageError);
    }

    [Fact]
    public void ConcurrentTransitions_LeaveAConsistentFile()
    {
        var queue = NewQueue(retention: 200);
        var jobs = queue.Enqueue(Enumerable.Range(0, 40).Select(index => NewJob("Job " + index)).ToList());

        Parallel.ForEach(jobs, job =>
        {
            queue.TryBegin(job.Id, out _, out _);
            queue.Complete(job.Id, "/exports/" + job.Id.ToString("N") + ".mkv");
        });

        var restarted = NewQueue(retention: 200).GetJobs();

        Assert.Equal(40, restarted.Count);
        Assert.All(restarted, job => Assert.Equal(ExportJobStatus.Completed, job.Status));
        Assert.All(restarted, job => Assert.False(string.IsNullOrEmpty(job.OutputPath)));
    }

    private sealed class ExplodingStore : IExportJobStore
    {
        public string? Location => "/nonexistent/jobs.json";

        public string? LastError { get; private set; }

        public IReadOnlyList<ExportJob> Load() => new List<ExportJob>();

        public void Save(IReadOnlyList<ExportJob> jobs) => LastError = "disk is full";
    }
}
