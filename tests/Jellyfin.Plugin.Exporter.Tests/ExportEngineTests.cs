using Jellyfin.Plugin.Exporter.Export;
using Jellyfin.Plugin.Exporter.Models;
using Jellyfin.Plugin.Exporter.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.Exporter.Tests;

/// <summary>
/// Runs the real export engine against real FFmpeg binaries. The tests are
/// skipped (return early) on hosts without FFmpeg; nothing is mocked.
/// </summary>
[CollectionDefinition("ffmpeg", DisableParallelization = true)]
public class FfmpegCollection
{
}

[Collection("ffmpeg")]
public class ExportEngineTests
{
    private static readonly IFfmpegLocator SoftwareLocator = new FixedLocator(
        new FfmpegLocation(TestMedia.FfmpegPath, TestMedia.FfprobePath, HwAccelPlan.Software, SvtAv1Available()));

    private static bool SvtAv1Available() => TestMedia.Available && TestMedia.HasEncoder("libsvtav1");

    private static ExportEngine NewEngine(IFfmpegLocator? locator = null) =>
        new(locator ?? SoftwareLocator, new PathReservationRegistry(), NullLogger<ExportEngine>.Instance);

    private static ExportJob Job(
        string outputDirectory,
        ExportFormat format,
        ExportResolution resolution,
        ExportAudioChannels audio) => new()
    {
        JellyfinItemId = Guid.NewGuid(),
        ItemName = "job",
        ItemType = "Movie",
        ProfileName = "Test",
        OutputDirectory = outputDirectory,
        Format = format,
        Resolution = resolution,
        AudioChannels = audio
    };

    private static IEnumerable<string> PartialFiles(string directory) =>
        Directory.Exists(directory)
            ? Directory.GetFiles(directory, "*.partial", SearchOption.AllDirectories)
            : Array.Empty<string>();

    [Fact]
    public async Task MovieExport_ProducesTheDocumentedLayoutAndARealH264File()
    {
        if (!TestMedia.Available)
        {
            return;
        }

        using var directory = new TestMedia.TempDirectory();
        var source = TestMedia.CreateSource(directory.Path, "movie", 640, 360, 6);
        var output = directory.Combine("exports");
        var item = ExportItemInfo.ForMovie(source, "Test: Movie/One", 2020);
        var progress = new List<double>();

        var result = await NewEngine().RunAsync(
            Job(output, ExportFormat.H264, ExportResolution.Resolution1080p, ExportAudioChannels.Original),
            item,
            progress.Add,
            CancellationToken.None);

        Assert.Equal(ExportJobStatus.Completed, result.Outcome);
        var expected = Path.Combine(output, "Movies", "Test- Movie-One (2020)", "Test- Movie-One (2020).mkv");
        Assert.Equal(expected, result.OutputPath);
        Assert.True(File.Exists(expected));
        Assert.Empty(PartialFiles(output));

        var probe = TestMedia.Probe(expected);
        Assert.Equal("h264", probe.VideoCodec);
        Assert.Equal(640, probe.Width);
        Assert.Equal(360, probe.Height);
        Assert.True(probe.HasAudio);
        Assert.Equal(6, probe.AudioChannels);
        Assert.Equal("aac", probe.AudioCodec);
        Assert.Equal(100, progress[^1]);
    }

    [Fact]
    public async Task EpisodeExport_ProducesTheDocumentedLayoutAndARealH265File()
    {
        if (!TestMedia.Available || !TestMedia.HasEncoder("libx265"))
        {
            return;
        }

        using var directory = new TestMedia.TempDirectory();
        var source = TestMedia.CreateSource(directory.Path, "episode", 1280, 720, 2);
        var output = directory.Combine("exports");
        var item = ExportItemInfo.ForEpisode(source, "Test Show", 2, 3, "The Third");

        var result = await NewEngine().RunAsync(
            Job(output, ExportFormat.H265, ExportResolution.Resolution720p, ExportAudioChannels.Stereo),
            item,
            null,
            CancellationToken.None);

        Assert.Equal(ExportJobStatus.Completed, result.Outcome);
        var expected = Path.Combine(output, "TV", "Test Show", "Season 02", "03 - The Third.mkv");
        Assert.Equal(expected, result.OutputPath);
        Assert.True(File.Exists(expected));

        var probe = TestMedia.Probe(expected);
        Assert.Equal("hevc", probe.VideoCodec);
        Assert.Equal(1280, probe.Width);
        Assert.Equal(720, probe.Height);
        Assert.Equal(2, probe.AudioChannels);
        Assert.Equal("aac", probe.AudioCodec);
    }

    [Fact]
    public async Task SeasonExport_WritesEveryEpisodeIntoItsSeasonFolder()
    {
        if (!TestMedia.Available)
        {
            return;
        }

        using var directory = new TestMedia.TempDirectory();
        var source = TestMedia.CreateSource(directory.Path, "season-episode", 1280, 720, 2, seconds: 1);
        var output = directory.Combine("exports");
        var engine = NewEngine();
        var job = Job(output, ExportFormat.H264, ExportResolution.Resolution480p, ExportAudioChannels.Stereo);

        var names = new[] { "Pilot", "Second", "Third" };
        var results = new List<ExportRunResult>();
        for (var index = 0; index < names.Length; index++)
        {
            var item = ExportItemInfo.ForEpisode(source, "Test Show", 1, index + 1, names[index]);
            results.Add(await engine.RunAsync(job, item, null, CancellationToken.None));
        }

        Assert.All(results, result => Assert.Equal(ExportJobStatus.Completed, result.Outcome));

        var seasonFolder = Path.Combine(output, "TV", "Test Show", "Season 01");
        var files = Directory.GetFiles(seasonFolder).Select(Path.GetFileName).OrderBy(name => name).ToList();
        Assert.Equal(new[] { "01 - Pilot.mkv", "02 - Second.mkv", "03 - Third.mkv" }, files);

        foreach (var file in Directory.GetFiles(seasonFolder))
        {
            var probe = TestMedia.Probe(file);
            Assert.Equal(852, probe.Width);
            Assert.Equal(480, probe.Height);
            Assert.Equal(2, probe.AudioChannels);
        }
    }

    [Fact]
    public async Task EveryResolution_ProducesTheExpectedFrameSize()
    {
        if (!TestMedia.Available)
        {
            return;
        }

        using var directory = new TestMedia.TempDirectory();
        var source = TestMedia.CreateSource(directory.Path, "uhd", 3840, 2160, 2, seconds: 0.5);
        var output = directory.Combine("exports");
        var engine = NewEngine();
        var expected = new Dictionary<ExportResolution, (int Width, int Height)>
        {
            [ExportResolution.Resolution4K] = (3840, 2160),
            [ExportResolution.Resolution1080p] = (1920, 1080),
            [ExportResolution.Resolution720p] = (1280, 720),
            [ExportResolution.Resolution480p] = (852, 480)
        };

        foreach (var (resolution, size) in expected)
        {
            var item = ExportItemInfo.ForMovie(source, "Resolution " + resolution, 2024);
            var result = await engine.RunAsync(
                Job(output, ExportFormat.H264, resolution, ExportAudioChannels.Stereo),
                item,
                null,
                CancellationToken.None);

            Assert.Equal(ExportJobStatus.Completed, result.Outcome);
            var probe = TestMedia.Probe(result.OutputPath!);
            Assert.Equal(size.Width, probe.Width);
            Assert.Equal(size.Height, probe.Height);
        }
    }

    [Fact]
    public async Task AudioModes_CopyUpmixAndDownmixAsSpecified()
    {
        if (!TestMedia.Available)
        {
            return;
        }

        using var directory = new TestMedia.TempDirectory();
        var surround = TestMedia.CreateSource(directory.Path, "surround", 640, 360, 6, seconds: 1);
        var stereo = TestMedia.CreateSource(directory.Path, "stereo", 640, 360, 2, seconds: 1);
        var output = directory.Combine("exports");
        var engine = NewEngine();

        var copied = await engine.RunAsync(
            Job(output, ExportFormat.H264, ExportResolution.Resolution480p, ExportAudioChannels.Original),
            ExportItemInfo.ForMovie(surround, "Original Audio", 2021),
            null,
            CancellationToken.None);
        var upmixed = await engine.RunAsync(
            Job(output, ExportFormat.H264, ExportResolution.Resolution480p, ExportAudioChannels.Surround51),
            ExportItemInfo.ForMovie(stereo, "Upmixed Audio", 2021),
            null,
            CancellationToken.None);
        var downmixed = await engine.RunAsync(
            Job(output, ExportFormat.H264, ExportResolution.Resolution480p, ExportAudioChannels.Stereo),
            ExportItemInfo.ForMovie(surround, "Downmixed Audio", 2021),
            null,
            CancellationToken.None);

        var copiedProbe = TestMedia.Probe(copied.OutputPath!);
        Assert.Equal(6, copiedProbe.AudioChannels);
        Assert.Equal("aac", copiedProbe.AudioCodec);

        Assert.Equal(6, TestMedia.Probe(upmixed.OutputPath!).AudioChannels);
        Assert.Equal(2, TestMedia.Probe(downmixed.OutputPath!).AudioChannels);
    }

    [Fact]
    public async Task Av1Export_ProducesAV1WhenTheEncoderIsAvailable()
    {
        if (!TestMedia.Available || !(TestMedia.HasEncoder("libsvtav1") || TestMedia.HasEncoder("libaom-av1")))
        {
            return;
        }

        using var directory = new TestMedia.TempDirectory();
        var source = TestMedia.CreateSource(directory.Path, "av1", 640, 360, 2, seconds: 1);
        var output = directory.Combine("exports");

        var result = await NewEngine().RunAsync(
            Job(output, ExportFormat.AV1, ExportResolution.Resolution480p, ExportAudioChannels.Stereo),
            ExportItemInfo.ForMovie(source, "Av1 Movie", 2024),
            null,
            CancellationToken.None);

        Assert.Equal(ExportJobStatus.Completed, result.Outcome);
        var probe = TestMedia.Probe(result.OutputPath!);
        Assert.Equal("av1", probe.VideoCodec);
        Assert.Equal("aac", probe.AudioCodec);
        Assert.True(new FileInfo(result.OutputPath!).Length > 0);
    }

    [Fact]
    public async Task DuplicateNames_AreDeDuplicatedInsteadOfOverwritten()
    {
        if (!TestMedia.Available)
        {
            return;
        }

        using var directory = new TestMedia.TempDirectory();
        var source = TestMedia.CreateSource(directory.Path, "dupe", 320, 240, 2, seconds: 1);
        var output = directory.Combine("exports");
        var engine = NewEngine();
        var item = ExportItemInfo.ForMovie(source, "Same Movie", 2020);
        var job = Job(output, ExportFormat.H264, ExportResolution.Resolution480p, ExportAudioChannels.Stereo);

        var first = await engine.RunAsync(job, item, null, CancellationToken.None);
        var second = await engine.RunAsync(job, item, null, CancellationToken.None);

        Assert.Equal(ExportJobStatus.Completed, first.Outcome);
        Assert.Equal(ExportJobStatus.Completed, second.Outcome);
        Assert.NotEqual(first.OutputPath, second.OutputPath);
        Assert.True(File.Exists(first.OutputPath));
        Assert.True(File.Exists(second.OutputPath));
        Assert.EndsWith("Same Movie (2020) (1).mkv", second.OutputPath);
    }

    [Fact]
    public async Task AFailingFfmpegRun_ReturnsFailureWithStderrAndLeavesNoPartialFile()
    {
        if (!TestMedia.Available)
        {
            return;
        }

        using var directory = new TestMedia.TempDirectory();
        var source = TestMedia.CreateSource(directory.Path, "gpu", 640, 360, 2, seconds: 1);
        var output = directory.Combine("exports");

        // A hardware plan on a machine without that hardware makes FFmpeg fail
        // after it started, which is the failure mode the engine must report.
        var locator = new FixedLocator(new FfmpegLocation(
            TestMedia.FfmpegPath,
            TestMedia.FfprobePath,
            new HwAccelPlan(HwAccelKind.Cuda),
            SvtAv1Available()));

        var result = await NewEngine(locator).RunAsync(
            Job(output, ExportFormat.H264, ExportResolution.Resolution480p, ExportAudioChannels.Stereo),
            ExportItemInfo.ForMovie(source, "Broken Encode", 2024),
            null,
            CancellationToken.None);

        Assert.Equal(ExportJobStatus.Failed, result.Outcome);
        Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
        Assert.Contains("FFmpeg exited with code", result.ErrorMessage);
        Assert.Empty(PartialFiles(output));

        // A failure must leave no artifact at all, not just no files: the
        // destination folder and the Movies/TV skeleton it created are gone.
        Assert.False(Directory.Exists(output));
        Assert.False(Directory.Exists(Path.Combine(output, "Movies", "Broken Encode (2024)")));
    }

    [Fact]
    public async Task AFailingEpisodeExport_RemovesTheSeasonFolderAndItsEmptyParents()
    {
        if (!TestMedia.Available)
        {
            return;
        }

        using var directory = new TestMedia.TempDirectory();
        var source = TestMedia.CreateSource(directory.Path, "gpu-episode", 640, 360, 2, seconds: 1);
        var output = directory.Combine("exports");
        var locator = new FixedLocator(new FfmpegLocation(
            TestMedia.FfmpegPath,
            TestMedia.FfprobePath,
            new HwAccelPlan(HwAccelKind.Cuda),
            SvtAv1Available()));

        var result = await NewEngine(locator).RunAsync(
            Job(output, ExportFormat.H264, ExportResolution.Resolution480p, ExportAudioChannels.Stereo),
            ExportItemInfo.ForEpisode(source, "Broken Show", 3, 7, "Broken Episode"),
            null,
            CancellationToken.None);

        Assert.Equal(ExportJobStatus.Failed, result.Outcome);
        Assert.False(Directory.Exists(output));
        Assert.False(Directory.Exists(Path.Combine(output, "TV", "Broken Show", "Season 03")));
    }

    [Fact]
    public async Task AFailingExport_KeepsAFolderThatHoldsAnotherJobsFile()
    {
        if (!TestMedia.Available)
        {
            return;
        }

        using var directory = new TestMedia.TempDirectory();
        var source = TestMedia.CreateSource(directory.Path, "gpu-shared", 640, 360, 2, seconds: 1);
        var output = directory.Combine("exports");

        // The same destination folder already holds a finished episode from
        // another job; the failure must not take the folder with it.
        var destination = Path.Combine(output, "TV", "Shared Show", "Season 01");
        Directory.CreateDirectory(destination);
        var finished = Path.Combine(destination, "01 - Pilot.mkv");
        File.WriteAllText(finished, "another job's export");

        var locator = new FixedLocator(new FfmpegLocation(
            TestMedia.FfmpegPath,
            TestMedia.FfprobePath,
            new HwAccelPlan(HwAccelKind.Cuda),
            SvtAv1Available()));

        var result = await NewEngine(locator).RunAsync(
            Job(output, ExportFormat.H264, ExportResolution.Resolution480p, ExportAudioChannels.Stereo),
            ExportItemInfo.ForEpisode(source, "Shared Show", 1, 2, "Broken Second"),
            null,
            CancellationToken.None);

        Assert.Equal(ExportJobStatus.Failed, result.Outcome);
        Assert.True(Directory.Exists(destination));
        Assert.Equal("another job's export", File.ReadAllText(finished));
        Assert.Equal(new[] { finished }, Directory.GetFiles(destination));
        Assert.Empty(PartialFiles(output));
    }

    [Fact]
    public async Task ACompletedExport_KeepsItsFolders()
    {
        if (!TestMedia.Available)
        {
            return;
        }

        using var directory = new TestMedia.TempDirectory();
        var source = TestMedia.CreateSource(directory.Path, "kept", 640, 360, 2, seconds: 1);
        var output = directory.Combine("exports");

        var result = await NewEngine().RunAsync(
            Job(output, ExportFormat.H264, ExportResolution.Resolution480p, ExportAudioChannels.Stereo),
            ExportItemInfo.ForMovie(source, "Kept Movie", 2022),
            null,
            CancellationToken.None);

        Assert.Equal(ExportJobStatus.Completed, result.Outcome);
        Assert.True(Directory.Exists(Path.Combine(output, "Movies", "Kept Movie (2022)")));
        Assert.True(File.Exists(result.OutputPath));
    }

    [Fact]
    public async Task AMissingSourceFile_FailsBeforeAnythingIsWritten()
    {
        if (!TestMedia.Available)
        {
            return;
        }

        using var directory = new TestMedia.TempDirectory();
        var output = directory.Combine("exports");

        // ffprobe cannot read the file, so the engine throws; the worker turns
        // this into a Failed job with the message.
        await Assert.ThrowsAsync<ExportException>(() => NewEngine().RunAsync(
            Job(output, ExportFormat.H264, ExportResolution.Resolution480p, ExportAudioChannels.Stereo),
            ExportItemInfo.ForMovie(Path.Combine(directory.Path, "gone.mkv"), "Missing", 2024),
            null,
            CancellationToken.None));
    }

    [Fact]
    public async Task CancellingAnExport_KillsFfmpegAndLeavesNoPartialFile()
    {
        if (!TestMedia.Available)
        {
            return;
        }

        using var directory = new TestMedia.TempDirectory();
        var source = TestMedia.CreateSource(directory.Path, "long", 1280, 720, 2, seconds: 30);
        var output = directory.Combine("exports");
        using var cancellation = new CancellationTokenSource();

        var result = await NewEngine().RunAsync(
            Job(output, ExportFormat.H264, ExportResolution.Resolution1080p, ExportAudioChannels.Stereo),
            ExportItemInfo.ForMovie(source, "Cancelled Movie", 2024),
            percent =>
            {
                if (percent > 0)
                {
                    cancellation.Cancel();
                }
            },
            cancellation.Token);

        Assert.Equal(ExportJobStatus.Cancelled, result.Outcome);
        Assert.Empty(PartialFiles(output));

        // Cancelling must leave the tree exactly as it was: no movie folder and
        // no Movies skeleton.
        Assert.False(Directory.Exists(output));
        Assert.False(Directory.Exists(Path.Combine(output, "Movies", "Cancelled Movie (2024)")));
    }

    [Fact]
    public async Task CancellingAnEpisodeExport_RemovesTheSeasonFolderAndItsEmptyParents()
    {
        if (!TestMedia.Available)
        {
            return;
        }

        using var directory = new TestMedia.TempDirectory();
        var source = TestMedia.CreateSource(directory.Path, "long-episode", 1280, 720, 2, seconds: 30);
        var output = directory.Combine("exports");
        using var cancellation = new CancellationTokenSource();

        var result = await NewEngine().RunAsync(
            Job(output, ExportFormat.H264, ExportResolution.Resolution1080p, ExportAudioChannels.Stereo),
            ExportItemInfo.ForEpisode(source, "Long Show", 2, 4, "The Long One"),
            percent =>
            {
                if (percent > 0)
                {
                    cancellation.Cancel();
                }
            },
            cancellation.Token);

        Assert.Equal(ExportJobStatus.Cancelled, result.Outcome);
        Assert.Empty(PartialFiles(output));
        Assert.False(Directory.Exists(output));
        Assert.False(Directory.Exists(Path.Combine(output, "TV", "Long Show", "Season 02")));
    }

    [Fact]
    public async Task VaapiExport_UsesTheHardwareEncoderWhenTheDeviceActuallyWorks()
    {
        if (!TestMedia.Available)
        {
            return;
        }

        const string device = "/dev/dri/renderD128";
        if (!Directory.Exists("/dev/dri") || !File.Exists(device))
        {
            return;
        }

        try
        {
            TestMedia.Run(TestMedia.FfmpegPath, new[]
            {
                "-hide_banner", "-nostdin", "-loglevel", "error",
                "-vaapi_device", device,
                "-f", "lavfi", "-i", "color=c=black:s=256x144:r=1:d=1",
                "-vf", "format=nv12,hwupload",
                "-frames:v", "1", "-c:v", "h264_vaapi", "-f", "null", "-"
            });
        }
        catch (InvalidOperationException)
        {
            // No working VA-API device on this host (typical for CI).
            return;
        }

        using var directory = new TestMedia.TempDirectory();
        var source = TestMedia.CreateSource(directory.Path, "vaapi", 1280, 720, 2, seconds: 1);
        var output = directory.Combine("exports");
        var locator = new FixedLocator(new FfmpegLocation(
            TestMedia.FfmpegPath,
            TestMedia.FfprobePath,
            new HwAccelPlan(HwAccelKind.Vaapi, device),
            SvtAv1Available()));

        var result = await NewEngine(locator).RunAsync(
            Job(output, ExportFormat.H264, ExportResolution.Resolution720p, ExportAudioChannels.Stereo),
            ExportItemInfo.ForMovie(source, "Hardware Movie", 2024),
            null,
            CancellationToken.None);

        Assert.Equal(ExportJobStatus.Completed, result.Outcome);
        var probe = TestMedia.Probe(result.OutputPath!);
        Assert.Equal("h264", probe.VideoCodec);
        Assert.Equal(1280, probe.Width);
        Assert.Equal(720, probe.Height);
        Assert.Equal(2, probe.AudioChannels);
    }

    private sealed class FixedLocator : IFfmpegLocator
    {
        private readonly FfmpegLocation _location;

        public FixedLocator(FfmpegLocation location) => _location = location;

        public FfmpegLocation Locate() => _location;
    }
}
