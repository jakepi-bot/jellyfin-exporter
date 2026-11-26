using Jellyfin.Plugin.Exporter.Models;
using Jellyfin.Plugin.Exporter.Services;

namespace Jellyfin.Plugin.Exporter.Tests;

public class ExportQueueTests
{
    private static ExportJob NewJob() => new()
    {
        JellyfinItemId = Guid.NewGuid(),
        ItemName = "The Movie",
        ItemType = "Movie",
        ProfileName = "Default",
        OutputDirectory = "/exports"
    };

    [Fact]
    public void Enqueue_QueuesJobsAndRaisesOneEventPerJob()
    {
        var queue = new ExportQueue();
        var events = new List<(Guid Id, ExportJobStatus Status)>();
        queue.JobStatusChanged += (_, args) => events.Add((args.Job.Id, args.Job.Status));

        var added = queue.Enqueue(new[] { NewJob(), NewJob() });

        Assert.Equal(2, added.Count);
        Assert.All(added, job => Assert.Equal(ExportJobStatus.Queued, job.Status));
        Assert.All(added, job => Assert.NotEqual(Guid.Empty, job.Id));
        Assert.Equal(2, events.Count);
        Assert.Equal(2, queue.GetJobs().Count);
    }

    [Fact]
    public void Enqueue_AssignsIdsAndIgnoresDuplicates()
    {
        var queue = new ExportQueue();
        var job = NewJob();
        job.Id = Guid.Empty;

        var first = queue.Enqueue(new[] { job });
        var second = queue.Enqueue(new[] { job });

        Assert.Single(first);
        Assert.Empty(second);
        Assert.Single(queue.GetJobs());
    }

    [Fact]
    public void GetJobs_ReturnsCopiesTheCallerCannotUseToMutateTheQueue()
    {
        var queue = new ExportQueue();
        queue.Enqueue(new[] { NewJob() });

        var snapshot = queue.GetJobs();
        snapshot[0].Status = ExportJobStatus.Completed;
        snapshot[0].ProgressPercent = 100;

        var fresh = queue.GetJobs()[0];
        Assert.Equal(ExportJobStatus.Queued, fresh.Status);
        Assert.Equal(0, fresh.ProgressPercent);
    }

    [Fact]
    public void Cancel_MarksAQueuedJobCancelled()
    {
        var queue = new ExportQueue();
        var job = queue.Enqueue(new[] { NewJob() })[0];

        Assert.True(queue.Cancel(job.Id));

        var stored = queue.GetJobs()[0];
        Assert.Equal(ExportJobStatus.Cancelled, stored.Status);
        Assert.NotNull(stored.FinishedAt);
        Assert.False(queue.Cancel(job.Id));
    }

    [Fact]
    public void Cancel_CancelsTheTokenOfARunningJob()
    {
        var queue = new ExportQueue();
        var job = queue.Enqueue(new[] { NewJob() })[0];
        Assert.True(queue.TryBegin(job.Id, out _, out var token));

        Assert.True(queue.Cancel(job.Id));
        Assert.True(token.IsCancellationRequested);
    }

    [Fact]
    public void TryBegin_ClaimsOnlyQueuedJobs()
    {
        var queue = new ExportQueue();
        var job = queue.Enqueue(new[] { NewJob() })[0];

        Assert.True(queue.TryBegin(job.Id, out var running, out _));
        Assert.Equal(ExportJobStatus.Running, running.Status);
        Assert.NotNull(running.StartedAt);

        Assert.False(queue.TryBegin(job.Id, out _, out _));
    }

    [Fact]
    public void CompleteAndFail_RecordTerminalStateAndRaiseEvents()
    {
        var queue = new ExportQueue();
        var completed = queue.Enqueue(new[] { NewJob() })[0];
        var failed = queue.Enqueue(new[] { NewJob() })[0];
        var transitions = new List<ExportJobStatus>();
        queue.JobStatusChanged += (_, args) => transitions.Add(args.Job.Status);

        queue.TryBegin(completed.Id, out _, out _);
        queue.ReportProgress(completed.Id, 42);
        queue.Complete(completed.Id, "/exports/Movies/The Movie/The Movie.mkv");
        queue.TryBegin(failed.Id, out _, out _);
        queue.Fail(failed.Id, "FFmpeg exited with code 1: boom");

        var jobs = queue.GetJobs();
        var completedJob = jobs.Single(j => j.Id == completed.Id);
        var failedJob = jobs.Single(j => j.Id == failed.Id);

        Assert.Equal(ExportJobStatus.Completed, completedJob.Status);
        Assert.Equal(100, completedJob.ProgressPercent);
        Assert.Equal("/exports/Movies/The Movie/The Movie.mkv", completedJob.OutputPath);
        Assert.NotNull(completedJob.FinishedAt);

        Assert.Equal(ExportJobStatus.Failed, failedJob.Status);
        Assert.Equal("FFmpeg exited with code 1: boom", failedJob.ErrorMessage);

        Assert.Contains(ExportJobStatus.Running, transitions);
        Assert.Contains(ExportJobStatus.Completed, transitions);
        Assert.Contains(ExportJobStatus.Failed, transitions);
    }

    [Fact]
    public void Progress_IsIgnoredOnceTheJobIsTerminal()
    {
        var queue = new ExportQueue();
        var job = queue.Enqueue(new[] { NewJob() })[0];
        queue.TryBegin(job.Id, out _, out _);
        queue.Complete(job.Id, "/exports/out.mkv");

        queue.ReportProgress(job.Id, 10);

        Assert.Equal(100, queue.GetJobs()[0].ProgressPercent);
    }

    [Fact]
    public void AThrowingSubscriber_DoesNotBreakTheQueue()
    {
        var queue = new ExportQueue();
        queue.JobStatusChanged += (_, _) => throw new InvalidOperationException("subscriber exploded");
        var observed = 0;
        queue.JobStatusChanged += (_, _) => observed++;

        var job = queue.Enqueue(new[] { NewJob() })[0];
        queue.TryBegin(job.Id, out _, out _);
        queue.Complete(job.Id, "/exports/out.mkv");

        Assert.Equal(3, observed);
        Assert.Equal(ExportJobStatus.Completed, queue.GetJobs()[0].Status);
    }

    [Fact]
    public void Retention_PrunesOnlyFinishedJobs()
    {
        var queue = new ExportQueue(retentionLimit: 2);
        for (var index = 0; index < 5; index++)
        {
            var job = queue.Enqueue(new[] { NewJob() })[0];
            queue.TryBegin(job.Id, out _, out _);
            queue.Complete(job.Id, $"/exports/{index}.mkv");
        }

        queue.Enqueue(new[] { NewJob() });

        var jobs = queue.GetJobs();
        Assert.Equal(3, jobs.Count);
        Assert.Single(jobs, job => job.Status == ExportJobStatus.Queued);
        Assert.Equal(2, jobs.Count(job => job.Status == ExportJobStatus.Completed));
    }

    [Fact]
    public void EventSubscribers_ReceiveCopies()
    {
        var queue = new ExportQueue();
        ExportJob? seen = null;
        queue.JobStatusChanged += (_, args) =>
        {
            seen = args.Job;
            args.Job.Status = ExportJobStatus.Failed;
        };

        queue.Enqueue(new[] { NewJob() });

        Assert.NotNull(seen);
        Assert.Equal(ExportJobStatus.Queued, queue.GetJobs()[0].Status);
    }
}
