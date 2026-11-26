using Jellyfin.Plugin.Exporter.Models;
using Jellyfin.Plugin.Exporter.Services;

namespace Jellyfin.Plugin.Exporter.Tests;

public class JsonExportJobStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "jfx-jobs-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }
    }

    private static ExportJob SampleJob(ExportJobStatus status = ExportJobStatus.Queued) => new()
    {
        JellyfinItemId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
        ItemName = "The Movie",
        ItemType = "Movie",
        ProfileId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
        ProfileName = "Archive 1080p",
        Status = status,
        ProgressPercent = status == ExportJobStatus.Completed ? 100 : 42.5,
        OutputPath = status == ExportJobStatus.Completed ? "/exports/Movies/The Movie (2020)/The Movie (2020).mkv" : null,
        ErrorMessage = status == ExportJobStatus.Failed ? "FFmpeg exited with code 1" : null,
        CreatedAt = DateTimeOffset.Parse("2026-11-26T10:00:00.0000000+00:00"),
        StartedAt = DateTimeOffset.Parse("2026-11-26T10:00:05.0000000+00:00"),
        FinishedAt = status == ExportJobStatus.Running ? null : DateTimeOffset.Parse("2026-11-26T10:04:00.0000000+00:00"),
        Format = ExportFormat.H265,
        Resolution = ExportResolution.Resolution720p,
        AudioChannels = ExportAudioChannels.Stereo,
        OutputDirectory = "/exports"
    };

    [Fact]
    public void Location_IsTheJobsFileInThePluginDataFolder()
    {
        var store = new JsonExportJobStore(_directory);

        Assert.Equal(Path.Combine(_directory, JsonExportJobStore.FileName), store.Location);
    }

    [Fact]
    public void Location_IsNullWithoutADirectory()
    {
        var store = new JsonExportJobStore((string?)null);

        Assert.Null(store.Location);
        Assert.Empty(store.Load());
        store.Save(new[] { SampleJob() });
        Assert.Null(store.LastError);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsEveryField()
    {
        var store = new JsonExportJobStore(_directory);
        var job = SampleJob(ExportJobStatus.Completed);

        store.Save(new[] { job });
        var loaded = Assert.Single(store.Load());

        Assert.Equal(job.Id, loaded.Id);
        Assert.Equal(job.JellyfinItemId, loaded.JellyfinItemId);
        Assert.Equal(job.ItemName, loaded.ItemName);
        Assert.Equal(job.ItemType, loaded.ItemType);
        Assert.Equal(job.ProfileId, loaded.ProfileId);
        Assert.Equal(job.ProfileName, loaded.ProfileName);
        Assert.Equal(ExportJobStatus.Completed, loaded.Status);
        Assert.Equal(100, loaded.ProgressPercent);
        Assert.Equal(job.OutputPath, loaded.OutputPath);
        Assert.Null(loaded.ErrorMessage);
        Assert.Equal(job.CreatedAt, loaded.CreatedAt);
        Assert.Equal(job.StartedAt, loaded.StartedAt);
        Assert.Equal(job.FinishedAt, loaded.FinishedAt);
        Assert.Equal(ExportFormat.H265, loaded.Format);
        Assert.Equal(ExportResolution.Resolution720p, loaded.Resolution);
        Assert.Equal(ExportAudioChannels.Stereo, loaded.AudioChannels);
        Assert.Equal("/exports", loaded.OutputDirectory);
        Assert.Null(store.LastError);
    }

    [Fact]
    public void Save_WritesEnumsByNameSoTheFileStaysReadable()
    {
        var store = new JsonExportJobStore(_directory);
        store.Save(new[] { SampleJob(ExportJobStatus.Completed) });

        var json = File.ReadAllText(store.Location!);

        Assert.Contains("\"Completed\"", json);
        Assert.Contains("\"H265\"", json);
        Assert.Contains("\"Resolution720p\"", json);
        Assert.Contains("\"Version\": 1", json);
    }

    [Fact]
    public void Save_CreatesTheDataFolder()
    {
        var nested = Path.Combine(_directory, "data", "plugins", "Jellyfin Exporter");
        var store = new JsonExportJobStore(nested);

        store.Save(new[] { SampleJob() });

        Assert.True(File.Exists(Path.Combine(nested, JsonExportJobStore.FileName)));
        Assert.Null(store.LastError);
    }

    [Fact]
    public void Save_ReplacesThePreviousFileAndLeavesNoTemporaryFile()
    {
        var store = new JsonExportJobStore(_directory);

        store.Save(new[] { SampleJob() });
        store.Save(new[] { SampleJob(ExportJobStatus.Failed), SampleJob(ExportJobStatus.Completed) });

        Assert.Equal(2, store.Load().Count);
        Assert.False(File.Exists(store.Location + ".tmp"));
    }

    [Fact]
    public void Load_WithoutAFileReturnsEmpty()
    {
        var store = new JsonExportJobStore(Path.Combine(_directory, "missing"));

        Assert.Empty(store.Load());
        Assert.Null(store.LastError);
    }

    [Fact]
    public void Load_QuarantinesACorruptFileInsteadOfThrowing()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, JsonExportJobStore.FileName);
        File.WriteAllText(path, "{ this is not json");

        var store = new JsonExportJobStore(_directory);
        var loaded = store.Load();

        Assert.Empty(loaded);
        Assert.NotNull(store.LastError);
        Assert.False(File.Exists(path));
        Assert.Single(Directory.GetFiles(_directory, "jobs.corrupt-*.json"));
    }

    [Fact]
    public void Save_ReportsAProblemWithoutThrowingWhenThePathIsUnusable()
    {
        // A file where the directory should be: creating the data folder fails.
        Directory.CreateDirectory(_directory);
        var blocker = Path.Combine(_directory, "blocked");
        File.WriteAllText(blocker, "not a directory");

        var store = new JsonExportJobStore(Path.Combine(blocker, "data"));

        store.Save(new[] { SampleJob() });

        Assert.NotNull(store.LastError);
    }

    [Fact]
    public void Load_SkipsJobsWithoutAnId()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, JsonExportJobStore.FileName);
        File.WriteAllText(
            path,
            "{\"Version\":1,\"Jobs\":[{\"Id\":\"00000000-0000-0000-0000-000000000000\",\"ItemName\":\"bad\"}," +
            "{\"Id\":\"11111111-2222-3333-4444-555555555555\",\"ItemName\":\"good\"}]}");

        var loaded = new JsonExportJobStore(_directory).Load();

        var job = Assert.Single(loaded);
        Assert.Equal("good", job.ItemName);
    }

    [Fact]
    public void Load_AcceptsTheUndashedGuidsJellyfinUses()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, JsonExportJobStore.FileName);
        File.WriteAllText(
            path,
            "{\"Version\":1,\"Jobs\":[{" +
            "\"Id\":\"ffffffffffffffffffffffffffffffff\"," +
            "\"JellyfinItemId\":\"e65358ead88d9d5e9625964773399452\"," +
            "\"ProfileId\":\"02420515144a4d6688112a914ee97d87\"," +
            "\"ItemName\":\"Test Movie (2020)\",\"Status\":\"Failed\"}]}");

        var store = new JsonExportJobStore(_directory);
        var job = Assert.Single(store.Load());

        Assert.Null(store.LastError);
        Assert.Equal(Guid.Parse("e65358ea-d88d-9d5e-9625-964773399452"), job.JellyfinItemId);
        Assert.Equal(Guid.Parse("02420515-144a-4d66-8811-2a914ee97d87"), job.ProfileId);
        Assert.Equal(ExportJobStatus.Failed, job.Status);
    }
}
