using Jellyfin.Plugin.Exporter.Api;
using Jellyfin.Plugin.Exporter.Models;
using Jellyfin.Plugin.Exporter.Services;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Exporter.Tests;

public class ExporterJobsControllerTests
{
    private static ExportJob NewJob(string name = "The Movie", string profile = "Default") => new()
    {
        JellyfinItemId = Guid.NewGuid(),
        ItemName = name,
        ItemType = "Movie",
        ProfileName = profile,
        Format = ExportFormat.H265,
        Resolution = ExportResolution.Resolution720p,
        AudioChannels = ExportAudioChannels.Stereo,
        OutputDirectory = "/exports"
    };

    private static (ExporterJobsController Controller, ExportQueue Queue) Create(int retention = 200)
    {
        var queue = new ExportQueue(retention);
        return (new ExporterJobsController(queue), queue);
    }

    private static JobsResponse Payload(ActionResult<JobsResponse> result)
    {
        Assert.NotNull(result.Value);
        return result.Value;
    }

    private static ExportJobView Payload(ActionResult<ExportJobView> result)
    {
        Assert.NotNull(result.Value);
        return result.Value;
    }

    [Fact]
    public void Get_ReturnsEveryJobWithTheCountsThePageNeeds()
    {
        var (controller, queue) = Create();
        var completed = queue.Enqueue(new[] { NewJob("Done") })[0];
        queue.TryBegin(completed.Id, out _, out _);
        queue.Complete(completed.Id, "/exports/Done.mkv");

        var failed = queue.Enqueue(new[] { NewJob("Broken") })[0];
        queue.TryBegin(failed.Id, out _, out _);
        queue.Fail(failed.Id, "FFmpeg exited with code 1: boom");

        queue.Enqueue(new[] { NewJob("Waiting") });

        var response = Payload(controller.Get());

        Assert.Equal(3, response.Jobs.Count);
        Assert.Equal(1, response.Counts.Completed);
        Assert.Equal(1, response.Counts.Failed);
        Assert.Equal(1, response.Counts.Queued);
        Assert.Equal(3, response.Counts.Total);
        Assert.Equal(200, response.HistoryLimit);
        Assert.False(string.IsNullOrEmpty(response.ServerTimeUtc));
    }

    [Fact]
    public void Get_ProjectsTheFieldsThePageRenders()
    {
        var (controller, queue) = Create();
        var job = queue.Enqueue(new[] { NewJob("The Movie") })[0];
        queue.TryBegin(job.Id, out _, out _);
        queue.ReportProgress(job.Id, 42.44);
        queue.Complete(job.Id, "/exports/The Movie.mkv");

        var view = Assert.Single(Payload(controller.Get()).Jobs);

        Assert.Equal(job.Id.ToString("D"), view.Id);
        Assert.Equal(job.JellyfinItemId.ToString("D"), view.JellyfinItemId);
        Assert.Equal("The Movie", view.ItemName);
        Assert.Equal("Default", view.ProfileName);
        Assert.Equal(ExportJobStatus.Completed, view.Status);
        Assert.Equal(100, view.ProgressPercent);
        Assert.Equal("/exports/The Movie.mkv", view.OutputPath);
        Assert.Equal("H265", view.Format);
        Assert.Equal("Resolution720p", view.Resolution);
        Assert.Equal("Stereo", view.AudioChannels);
        Assert.False(string.IsNullOrEmpty(view.CreatedAt));
        Assert.False(string.IsNullOrEmpty(view.StartedAt));
        Assert.False(string.IsNullOrEmpty(view.FinishedAt));
        Assert.False(view.CanCancel);
        Assert.True(view.CanRetry);
    }

    [Fact]
    public void Get_PutsActiveJobsBeforeFinishedOnes()
    {
        var (controller, queue) = Create();
        var finished = queue.Enqueue(new[] { NewJob("Finished") })[0];
        queue.TryBegin(finished.Id, out _, out _);
        queue.Complete(finished.Id, "/exports/finished.mkv");
        queue.Enqueue(new[] { NewJob("Waiting") });

        var response = Payload(controller.Get());

        Assert.Equal("Waiting", response.Jobs[0].ItemName);
        Assert.Equal("Finished", response.Jobs[1].ItemName);
    }

    [Theory]
    [InlineData("active", new[] { "Waiting" })]
    [InlineData("queued", new[] { "Waiting" })]
    [InlineData("finished", new[] { "Broken", "Finished" })]
    [InlineData("completed", new[] { "Finished" })]
    [InlineData("failed", new[] { "Broken" })]
    [InlineData("completed,failed", new[] { "Broken", "Finished" })]
    public void Get_FiltersByStatus(string filter, string[] expected)
    {
        var (controller, queue) = Create();
        var finished = queue.Enqueue(new[] { NewJob("Finished") })[0];
        queue.TryBegin(finished.Id, out _, out _);
        queue.Complete(finished.Id, "/exports/finished.mkv");
        var broken = queue.Enqueue(new[] { NewJob("Broken") })[0];
        queue.TryBegin(broken.Id, out _, out _);
        queue.Fail(broken.Id, "boom");
        queue.Enqueue(new[] { NewJob("Waiting") });

        var response = Payload(controller.Get(filter));

        Assert.Equal(expected, response.Jobs.Select(job => job.ItemName).ToArray());
    }

    [Fact]
    public void Get_RejectsAnUnknownFilter()
    {
        var (controller, _) = Create();

        var result = controller.Get("exploded").Result;

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public void Get_AppliesTheLimitAfterOrdering()
    {
        var (controller, queue) = Create();
        queue.Enqueue(Enumerable.Range(0, 5).Select(index => NewJob("Job " + index)).ToList());

        var response = Payload(controller.Get(limit: 2));

        Assert.Equal(2, response.Jobs.Count);
        Assert.Equal(5, response.Counts.Total);
    }

    [Fact]
    public void Get_ReportsTheHistoryFileSoOperatorsCanFindIt()
    {
        var (controller, queue) = Create();
        queue.Enqueue(new[] { NewJob() });

        var response = Payload(controller.Get());

        Assert.Null(response.HistoryFile);
        Assert.Null(response.StorageError);
    }

    [Fact]
    public void Cancel_StopsAQueuedJob()
    {
        var (controller, queue) = Create();
        var job = queue.Enqueue(new[] { NewJob() })[0];

        var view = Payload(controller.Cancel(job.Id.ToString("D")));

        Assert.Equal(ExportJobStatus.Cancelled, view.Status);
        Assert.Equal(ExportJobStatus.Cancelled, queue.GetJobs().Single().Status);
    }

    [Fact]
    public void Cancel_AcceptsAnIdWithoutDashes()
    {
        var (controller, queue) = Create();
        var job = queue.Enqueue(new[] { NewJob() })[0];

        var result = controller.Cancel(job.Id.ToString("N"));

        Assert.NotNull(result.Value);
    }

    [Fact]
    public void Cancel_RejectsAFinishedJob()
    {
        var (controller, queue) = Create();
        var job = queue.Enqueue(new[] { NewJob() })[0];
        queue.TryBegin(job.Id, out _, out _);
        queue.Complete(job.Id, "/exports/out.mkv");

        Assert.IsType<ConflictObjectResult>(controller.Cancel(job.Id.ToString()).Result);
    }

    [Fact]
    public void Cancel_Returns404ForAnUnknownJob()
    {
        var (controller, _) = Create();

        Assert.IsType<NotFoundObjectResult>(controller.Cancel(Guid.NewGuid().ToString()).Result);
        Assert.IsType<BadRequestObjectResult>(controller.Cancel("not-an-id").Result);
    }

    [Fact]
    public void Retry_QueuesACopyOfAFailedJobWithTheSameSnapshot()
    {
        var (controller, queue) = Create();
        var job = queue.Enqueue(new[] { NewJob("Broken") })[0];
        queue.TryBegin(job.Id, out _, out _);
        queue.Fail(job.Id, "FFmpeg exited with code 1: boom");

        var view = Payload(controller.Retry(job.Id.ToString()));

        Assert.NotEqual(job.Id, Guid.Parse(view.Id));
        Assert.Equal(ExportJobStatus.Queued, view.Status);
        Assert.Equal("Broken", view.ItemName);
        Assert.Equal(job.JellyfinItemId, Guid.Parse(view.JellyfinItemId));
        Assert.Equal("Default", view.ProfileName);
        Assert.Equal("H265", view.Format);
        Assert.Equal("Resolution720p", view.Resolution);
        Assert.Equal("/exports", view.OutputDirectory);

        var queuedJob = queue.GetJobs().Single(candidate => candidate.Id == Guid.Parse(view.Id));
        Assert.Equal(ExportJobStatus.Queued, queuedJob.Status);
        Assert.Equal(job.OutputDirectory, queuedJob.OutputDirectory);
    }

    [Fact]
    public void Retry_RefusesWhenAnIdenticalJobIsStillActive()
    {
        var (controller, queue) = Create();
        var job = queue.Enqueue(new[] { NewJob("Broken") })[0];
        queue.TryBegin(job.Id, out _, out _);
        queue.Fail(job.Id, "boom");

        var retried = Payload(controller.Retry(job.Id.ToString()));
        var newJobId = Guid.Parse(retried.Id);

        // The retried copy is queued; retrying the original again would create a
        // second job for the same item, profile and directory.
        var second = controller.Retry(job.Id.ToString()).Result;

        Assert.IsType<ConflictObjectResult>(second);
        Assert.Single(queue.GetJobs(), candidate => !candidate.IsTerminal);
        Assert.NotEqual(Guid.Empty, newJobId);
    }

    [Fact]
    public void Retry_RefusesAJobThatIsStillRunning()
    {
        var (controller, queue) = Create();
        var job = queue.Enqueue(new[] { NewJob() })[0];
        queue.TryBegin(job.Id, out _, out _);

        Assert.IsType<ConflictObjectResult>(controller.Retry(job.Id.ToString()).Result);
    }

    [Fact]
    public void Retry_Returns404ForAnUnknownJob()
    {
        var (controller, _) = Create();

        Assert.IsType<NotFoundObjectResult>(controller.Retry(Guid.NewGuid().ToString()).Result);
    }
}
