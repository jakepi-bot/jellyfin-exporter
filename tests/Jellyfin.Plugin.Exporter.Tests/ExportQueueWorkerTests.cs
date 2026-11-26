using Jellyfin.Plugin.Exporter.Export;
using Jellyfin.Plugin.Exporter.Models;
using Jellyfin.Plugin.Exporter.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.Exporter.Tests;

public class ExportQueueWorkerTests
{
    private static ExportJob NewJob(string name = "The Movie") => new()
    {
        JellyfinItemId = Guid.NewGuid(),
        ItemName = name,
        ItemType = "Movie",
        ProfileName = "Default",
        OutputDirectory = "/exports"
    };

    private static ExportQueueWorker CreateWorker(
        ExportQueue queue,
        IExportEngine engine,
        IExportSettings? settings = null,
        IExportItemResolver? resolver = null) =>
        new(
            queue,
            resolver ?? new FakeResolver(),
            engine,
            settings ?? new FixedSettings(1),
            NullLogger<ExportQueueWorker>.Instance);

    private static async Task WaitUntilAsync(Func<bool> condition, string description, int timeoutMs = 15000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException($"Timed out waiting for {description}.");
    }

    private static async Task RunWorkerAsync(ExportQueueWorker worker, Func<Task> body)
    {
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await body();
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Worker_RunsQueuedJobsAndCompletesThem()
    {
        var queue = new ExportQueue();
        var engine = new FakeEngine((job, _, _) => Task.FromResult(ExportRunResult.Completed("/exports/Movies/out.mkv")));
        var worker = CreateWorker(queue, engine);
        var job = queue.Enqueue(new[] { NewJob() })[0];

        await RunWorkerAsync(worker, async () =>
        {
            await WaitUntilAsync(
                () => queue.GetJobs().Single().Status == ExportJobStatus.Completed,
                "the job to complete");
        });

        var stored = queue.GetJobs().Single();
        Assert.Equal(ExportJobStatus.Completed, stored.Status);
        Assert.Equal("/exports/Movies/out.mkv", stored.OutputPath);
        Assert.NotNull(stored.StartedAt);
        Assert.NotNull(stored.FinishedAt);
        Assert.Equal(1, engine.Invocations);
    }

    [Fact]
    public async Task Worker_NeverRunsMoreThanMaxConcurrentExports()
    {
        var queue = new ExportQueue();
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = new FakeEngine(async (_, _, _) =>
        {
            await release.Task;
            return ExportRunResult.Completed("/exports/out.mkv");
        });
        var worker = CreateWorker(queue, engine, new FixedSettings(2));
        queue.Enqueue(new[] { NewJob("One"), NewJob("Two"), NewJob("Three"), NewJob("Four") });

        await RunWorkerAsync(worker, async () =>
        {
            await WaitUntilAsync(() => queue.GetJobs().Count(j => j.Status == ExportJobStatus.Running) == 2, "two running jobs");
            await Task.Delay(400);

            Assert.Equal(2, queue.GetJobs().Count(j => j.Status == ExportJobStatus.Running));
            Assert.Equal(2, queue.GetJobs().Count(j => j.Status == ExportJobStatus.Queued));
            Assert.Equal(2, engine.PeakConcurrency);

            release.SetResult(true);
            await WaitUntilAsync(
                () => queue.GetJobs().All(j => j.Status == ExportJobStatus.Completed),
                "all four jobs to complete");
        });

        Assert.Equal(2, engine.PeakConcurrency);
        Assert.Equal(4, engine.Invocations);
    }

    [Fact]
    public async Task Worker_MarksACancelledRunningJobCancelled()
    {
        var queue = new ExportQueue();
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = new FakeEngine(async (_, _, cancellationToken) =>
        {
            started.TrySetResult(true);
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return ExportRunResult.Completed("/exports/out.mkv");
        });
        var worker = CreateWorker(queue, engine);
        var job = queue.Enqueue(new[] { NewJob() })[0];

        await RunWorkerAsync(worker, async () =>
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(queue.Cancel(job.Id));
            await WaitUntilAsync(
                () => queue.GetJobs().Single().Status == ExportJobStatus.Cancelled,
                "the job to be marked cancelled");
        });

        Assert.Equal(ExportJobStatus.Cancelled, queue.GetJobs().Single().Status);
    }

    [Fact]
    public async Task Worker_ReportsProgressToTheQueue()
    {
        var queue = new ExportQueue();
        var engine = new FakeEngine((_, _, _) =>
        {
            return Task.FromResult(ExportRunResult.Completed("/exports/out.mkv"));
        })
        {
            OnRun = (_, progress, _) =>
            {
                progress?.Invoke(37.5);
                return Task.FromResult(ExportRunResult.Completed("/exports/out.mkv"));
            }
        };
        var worker = CreateWorker(queue, engine);
        queue.Enqueue(new[] { NewJob() });

        await RunWorkerAsync(worker, async () =>
        {
            await WaitUntilAsync(
                () => queue.GetJobs().Single().Status == ExportJobStatus.Completed,
                "the job to complete");
        });

        // Completion forces 100; the intermediate value proves the wiring reaches the queue.
        Assert.Equal(100, queue.GetJobs().Single().ProgressPercent);
        Assert.Contains(37.5, engine.ReportedProgress);
    }

    [Fact]
    public async Task Worker_MarksAJobFailedWhenTheItemCannotBeResolved()
    {
        var queue = new ExportQueue();
        var worker = CreateWorker(
            queue,
            new FakeEngine((_, _, _) => Task.FromResult(ExportRunResult.Completed("/exports/out.mkv"))),
            resolver: new ThrowingResolver("The media file is missing: /media/gone.mkv"));
        queue.Enqueue(new[] { NewJob() });

        await RunWorkerAsync(worker, async () =>
        {
            await WaitUntilAsync(
                () => queue.GetJobs().Single().Status == ExportJobStatus.Failed,
                "the job to fail");
        });

        var stored = queue.GetJobs().Single();
        Assert.Equal(ExportJobStatus.Failed, stored.Status);
        Assert.Equal("The media file is missing: /media/gone.mkv", stored.ErrorMessage);
    }

    [Fact]
    public async Task Worker_MarksAJobFailedWhenTheEngineThrows()
    {
        var queue = new ExportQueue();
        var engine = new FakeEngine((_, _, _) => throw new ExportException("ffprobe could not read the source"));
        var worker = CreateWorker(queue, engine);
        queue.Enqueue(new[] { NewJob() });

        await RunWorkerAsync(worker, async () =>
        {
            await WaitUntilAsync(
                () => queue.GetJobs().Single().Status == ExportJobStatus.Failed,
                "the job to fail");
        });

        Assert.Equal("ffprobe could not read the source", queue.GetJobs().Single().ErrorMessage);
    }

    [Fact]
    public async Task Worker_CancelsRunningExportsOnShutdown()
    {
        var queue = new ExportQueue();
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = new FakeEngine(async (_, _, cancellationToken) =>
        {
            started.TrySetResult(true);
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return ExportRunResult.Completed("/exports/out.mkv");
        });
        var worker = CreateWorker(queue, engine);
        queue.Enqueue(new[] { NewJob() });

        await worker.StartAsync(CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(ExportJobStatus.Cancelled, queue.GetJobs().Single().Status);
        Assert.Equal(0, worker.RunningCount);
    }

    private sealed class FixedSettings : IExportSettings
    {
        public FixedSettings(int maxConcurrentExports) => MaxConcurrentExports = maxConcurrentExports;

        public int MaxConcurrentExports { get; }
    }

    private sealed class FakeResolver : IExportItemResolver
    {
        public Task<ExportItemInfo> ResolveAsync(ExportJob job, CancellationToken cancellationToken) =>
            Task.FromResult(ExportItemInfo.ForMovie("/media/movie.mkv", job.ItemName, 2020));
    }

    private sealed class ThrowingResolver : IExportItemResolver
    {
        private readonly string _message;

        public ThrowingResolver(string message) => _message = message;

        public Task<ExportItemInfo> ResolveAsync(ExportJob job, CancellationToken cancellationToken) =>
            throw new ExportException(_message);
    }

    private sealed class FakeEngine : IExportEngine
    {
        private readonly Func<ExportJob, ExportItemInfo, CancellationToken, Task<ExportRunResult>> _handler;
        private int _active;
        private int _invocations;

        public FakeEngine(Func<ExportJob, ExportItemInfo, CancellationToken, Task<ExportRunResult>> handler)
        {
            _handler = handler;
        }

        public Func<ExportJob, Action<double>?, CancellationToken, Task<ExportRunResult>>? OnRun { get; init; }

        public int Invocations => _invocations;

        public int PeakConcurrency { get; private set; }

        public List<double> ReportedProgress { get; } = new();

        public async Task<ExportRunResult> RunAsync(
            ExportJob job,
            ExportItemInfo item,
            Action<double>? progress,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _invocations);
            var active = Interlocked.Increment(ref _active);
            PeakConcurrency = Math.Max(PeakConcurrency, active);

            try
            {
                if (OnRun is not null)
                {
                    return await OnRun(
                        job,
                        value =>
                        {
                            lock (ReportedProgress)
                            {
                                ReportedProgress.Add(value);
                            }

                            progress?.Invoke(value);
                        },
                        cancellationToken);
                }

                return await _handler(job, item, cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }
    }
}
