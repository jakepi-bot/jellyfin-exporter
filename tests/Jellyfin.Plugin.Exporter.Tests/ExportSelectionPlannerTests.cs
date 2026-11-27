using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Exporter.Models;
using Jellyfin.Plugin.Exporter.Services;
using Xunit;

namespace Jellyfin.Plugin.Exporter.Tests;

public class ExportSelectionPlannerTests
{
    private static readonly ExportProfile Profile = new()
    {
        Id = Guid.NewGuid(),
        Name = "1080p H264",
        Format = ExportFormat.H264,
        Resolution = ExportResolution.Resolution1080p,
        AudioChannels = ExportAudioChannels.Stereo,
        OutputDirectory = "/media/exports"
    };

    private static MediaTreeNode Episode(string name, int season, int number, Guid? id = null) => new(
        id ?? Guid.NewGuid(),
        name,
        "Episode",
        "The Wire",
        season,
        number);

    [Fact]
    public void Plan_SnapshotsTheProfileOntoEveryJob()
    {
        var item = new MediaTreeNode(Guid.NewGuid(), "Heat", "Movie");

        var plan = ExportSelectionPlanner.Plan(Profile, new[] { item }, Array.Empty<ExportJob>());

        var job = Assert.Single(plan.Jobs);
        Assert.Equal(item.Id, job.JellyfinItemId);
        Assert.Equal("Movie", job.ItemType);
        Assert.Equal(Profile.Id, job.ProfileId);
        Assert.Equal(Profile.Name, job.ProfileName);
        Assert.Equal(Profile.Format, job.Format);
        Assert.Equal(Profile.Resolution, job.Resolution);
        Assert.Equal(Profile.AudioChannels, job.AudioChannels);
        Assert.Equal(Profile.OutputDirectory, job.OutputDirectory);
        Assert.Equal(Profile.UseJellyfinTranscodingSettings, job.UseJellyfinTranscodingSettings);
        Assert.Equal(ExportJobStatus.Queued, job.Status);
        Assert.NotEqual(Guid.Empty, job.Id);
    }

    [Fact]
    public void Plan_SnapshotsTheTranscodingSettingsToggle()
    {
        var profile = new ExportProfile
        {
            Id = Guid.NewGuid(),
            Name = "Plugin settings only",
            Format = ExportFormat.H264,
            OutputDirectory = "/media/exports",
            UseJellyfinTranscodingSettings = false
        };
        var item = new MediaTreeNode(Guid.NewGuid(), "Heat", "Movie");

        var plan = ExportSelectionPlanner.Plan(profile, new[] { item }, Array.Empty<ExportJob>());

        Assert.False(Assert.Single(plan.Jobs).UseJellyfinTranscodingSettings);
    }

    [Fact]
    public void Plan_QueuesOneJobPerEpisode_WithReadableNames()
    {
        var items = new[]
        {
            Episode("The Target", 1, 1),
            Episode("The Detail", 1, 2)
        };

        var plan = ExportSelectionPlanner.Plan(Profile, items, Array.Empty<ExportJob>());

        Assert.Equal(2, plan.Jobs.Count);
        Assert.Equal("The Wire S01E01 - The Target", plan.Jobs[0].ItemName);
        Assert.Equal("The Wire S01E02 - The Detail", plan.Jobs[1].ItemName);
    }

    [Fact]
    public void Plan_SkipsItemsAlreadyQueuedForTheSameProfile()
    {
        var queued = Episode("The Target", 1, 1);
        var fresh = Episode("The Detail", 1, 2);
        var existing = new ExportJob
        {
            JellyfinItemId = queued.Id,
            ProfileId = Profile.Id,
            Status = ExportJobStatus.Running
        };

        var plan = ExportSelectionPlanner.Plan(Profile, new[] { queued, fresh }, new[] { existing });

        var job = Assert.Single(plan.Jobs);
        Assert.Equal(fresh.Id, job.JellyfinItemId);
        var duplicate = Assert.Single(plan.Duplicates);
        Assert.Equal(queued.Id, duplicate.Id);
    }

    [Fact]
    public void Plan_DoesNotSkipItemsQueuedForAnotherProfile()
    {
        var item = Episode("The Target", 1, 1);
        var existing = new ExportJob
        {
            JellyfinItemId = item.Id,
            ProfileId = Guid.NewGuid(),
            Status = ExportJobStatus.Queued
        };

        var plan = ExportSelectionPlanner.Plan(Profile, new[] { item }, new[] { existing });

        Assert.Single(plan.Jobs);
        Assert.Empty(plan.Duplicates);
    }

    [Fact]
    public void Plan_DoesNotSkipFinishedJobs()
    {
        var item = Episode("The Target", 1, 1);
        var existing = new ExportJob
        {
            JellyfinItemId = item.Id,
            ProfileId = Profile.Id,
            Status = ExportJobStatus.Completed
        };

        var plan = ExportSelectionPlanner.Plan(Profile, new[] { item }, new[] { existing });

        Assert.Single(plan.Jobs);
        Assert.Empty(plan.Duplicates);
    }

    [Fact]
    public void Plan_CollapsesDuplicateItemsInTheRequest()
    {
        var item = Episode("The Target", 1, 1);

        var plan = ExportSelectionPlanner.Plan(Profile, new[] { item, item }, Array.Empty<ExportJob>());

        Assert.Single(plan.Jobs);
        Assert.Empty(plan.Duplicates);
    }

    [Fact]
    public void Plan_WithoutProfile_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            ExportSelectionPlanner.Plan(null, Array.Empty<MediaTreeNode>(), Array.Empty<ExportJob>()));
    }

    [Fact]
    public void Describe_FormatsEpisodeLabelsAndFallsBackToTheName()
    {
        Assert.Equal("The Wire S02E07 - One Arrest", ExportSelectionPlanner.Describe(Episode("One Arrest", 2, 7)));
        Assert.Equal("Heat", ExportSelectionPlanner.Describe(new MediaTreeNode(Guid.NewGuid(), "Heat", "Movie")));
        Assert.Equal("S01E03 - Pilot", ExportSelectionPlanner.Describe(
            new MediaTreeNode(Guid.NewGuid(), "Pilot", "Episode", null, 1, 3)));
    }
}
