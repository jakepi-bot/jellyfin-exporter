using Jellyfin.Plugin.Exporter.Export;
using Jellyfin.Plugin.Exporter.Models;

namespace Jellyfin.Plugin.Exporter.Tests;

public class FfmpegCommandBuilderTests
{
    private static readonly MediaProbeInfo Probe = new()
    {
        Width = 1920,
        Height = 1080,
        DurationSeconds = 120,
        HasAudio = true,
        AudioChannels = 6,
        AudioCodec = "eac3"
    };

    private static ExportJob Job(
        ExportFormat format = ExportFormat.H264,
        ExportResolution resolution = ExportResolution.Resolution1080p,
        ExportAudioChannels audio = ExportAudioChannels.Original) => new()
    {
        JellyfinItemId = Guid.NewGuid(),
        ItemName = "The Movie",
        ItemType = "Movie",
        OutputDirectory = "/exports",
        Format = format,
        Resolution = resolution,
        AudioChannels = audio
    };

    private static FfmpegCommand Build(
        ExportJob job,
        HwAccelPlan? plan = null,
        bool svtAv1 = true,
        MediaProbeInfo? probe = null) =>
        FfmpegCommandBuilder.Build(job, probe ?? Probe, "/media/source.mkv", "/exports/temp.partial", plan, svtAv1);

    private static string Arguments(FfmpegCommand command) => string.Join(' ', command.Arguments);

    [Fact]
    public void Build_MapsTheFirstVideoStreamAndAllAudioOptionally()
    {
        var arguments = Arguments(Build(Job()));

        Assert.Contains("-map 0:v:0", arguments);
        Assert.Contains("-map 0:a?", arguments);
        Assert.Contains("-progress pipe:1", arguments);
        Assert.Contains("-f matroska", arguments);
        Assert.EndsWith("/exports/temp.partial", arguments);
    }

    [Fact]
    public void Build_H264OriginalCopiesAudioAtTheScaledResolution()
    {
        var command = Build(Job(ExportFormat.H264, ExportResolution.Resolution720p, ExportAudioChannels.Original));
        var arguments = Arguments(command);

        Assert.Contains("-c:v libx264", arguments);
        Assert.Contains("-preset medium", arguments);
        Assert.Contains("-crf 20", arguments);
        Assert.Contains("-vf scale=1280:720", arguments);
        Assert.Contains("-c:a copy", arguments);
        Assert.Equal("libx264", command.VideoEncoder);
        Assert.Equal("copy", command.AudioCodec);
        Assert.False(command.HardwareVideoEncoder);
        Assert.Equal(1280, command.Width);
        Assert.Equal(720, command.Height);
    }

    [Fact]
    public void Build_H265StereoReEncodesToTwoChannelAac()
    {
        var arguments = Arguments(Build(Job(ExportFormat.H265, ExportResolution.Resolution1080p, ExportAudioChannels.Stereo)));

        Assert.Contains("-c:v libx265", arguments);
        Assert.Contains("-crf 24", arguments);
        Assert.Contains("-c:a aac -ac 2 -b:a 192k", arguments);
    }

    [Fact]
    public void Build_Surround51ReEncodesToSixChannelAac()
    {
        var command = Build(Job(ExportFormat.AV1, ExportResolution.Resolution480p, ExportAudioChannels.Surround51), svtAv1: false);
        var arguments = Arguments(command);

        Assert.Contains("-c:v libaom-av1", arguments);
        Assert.Contains("-c:a aac -ac 6 -b:a 384k", arguments);
        Assert.Equal("aac", command.AudioCodec);
    }

    [Fact]
    public void Build_PrefersSvtAv1ForSoftwareAv1()
    {
        Assert.Contains("-c:v libsvtav1", Arguments(Build(Job(ExportFormat.AV1), svtAv1: true)));
    }

    [Fact]
    public void Build_NvencPlanUsesNvencEncodersAndQualityOptions()
    {
        var command = Build(Job(ExportFormat.H265), new HwAccelPlan(HwAccelKind.Cuda));
        var arguments = Arguments(command);

        Assert.Contains("-c:v hevc_nvenc", arguments);
        Assert.Contains("-rc vbr", arguments);
        Assert.Contains("-cq 23", arguments);
        Assert.True(command.HardwareVideoEncoder);
        Assert.DoesNotContain("-vaapi_device", arguments);
    }

    [Fact]
    public void Build_VaapiPlanUploadsFramesToTheRenderDevice()
    {
        var command = Build(
            Job(ExportFormat.H264, ExportResolution.Resolution720p),
            new HwAccelPlan(HwAccelKind.Vaapi, "/dev/dri/renderD129"));
        var arguments = Arguments(command);

        Assert.Contains("-vaapi_device /dev/dri/renderD129", arguments);
        Assert.Contains("-c:v h264_vaapi", arguments);
        Assert.Contains("-qp 23", arguments);
        Assert.Contains("format=nv12,hwupload,scale_vaapi=w=1280:h=720", arguments);
    }

    [Fact]
    public void Build_QsvPlanUsesQsvEncoders()
    {
        var arguments = Arguments(Build(Job(ExportFormat.H264), new HwAccelPlan(HwAccelKind.Qsv)));

        Assert.Contains("-c:v h264_qsv", arguments);
        Assert.Contains("-global_quality 23", arguments);
    }

    [Fact]
    public void Build_VideoToolboxHasNoAv1EncoderSoAv1FallsBackToSoftware()
    {
        var command = Build(Job(ExportFormat.AV1), new HwAccelPlan(HwAccelKind.VideoToolbox));

        Assert.Contains("-c:v libsvtav1", Arguments(command));
        Assert.False(command.HardwareVideoEncoder);
    }

    [Fact]
    public void Build_DoesNotUpscaleSmallSources()
    {
        var command = Build(
            Job(ExportFormat.H264, ExportResolution.Resolution4K),
            probe: new MediaProbeInfo { Width = 720, Height = 480, DurationSeconds = 60 });

        Assert.Equal(720, command.Width);
        Assert.Equal(480, command.Height);
    }

    [Fact]
    public void Build_TargetsFourKWhenTheSourceIsLargeEnough()
    {
        var command = Build(
            Job(ExportFormat.H264, ExportResolution.Resolution4K),
            probe: new MediaProbeInfo { Width = 3840, Height = 2160, DurationSeconds = 60 });

        Assert.Equal(3840, command.Width);
        Assert.Equal(2160, command.Height);
        Assert.Contains("-vf scale=3840:2160", Arguments(command));
    }

    [Theory]
    [InlineData(ExportResolution.Resolution4K, 3840, 2160)]
    [InlineData(ExportResolution.Resolution1080p, 1920, 1080)]
    [InlineData(ExportResolution.Resolution720p, 1280, 720)]
    [InlineData(ExportResolution.Resolution480p, 854, 480)]
    public void GetTargetDimensions_MapsEveryProfileResolution(ExportResolution resolution, int width, int height)
    {
        Assert.Equal((width, height), FfmpegCommandBuilder.GetTargetDimensions(resolution));
    }

    [Theory]
    [InlineData(1920, 1080, 854, 480, 852, 480)]
    [InlineData(1920, 800, 1920, 1080, 1920, 800)]
    [InlineData(1280, 720, 3840, 2160, 1280, 720)]
    [InlineData(1440, 1080, 1920, 1080, 1440, 1080)]
    [InlineData(1919, 1079, 1920, 1080, 1918, 1078)]
    public void ScaleToFit_PreservesAspectRatioAndProducesEvenDimensions(
        int sourceWidth,
        int sourceHeight,
        int targetWidth,
        int targetHeight,
        int expectedWidth,
        int expectedHeight)
    {
        Assert.Equal(
            (expectedWidth, expectedHeight),
            FfmpegCommandBuilder.ScaleToFit(sourceWidth, sourceHeight, targetWidth, targetHeight));
    }

    [Fact]
    public void Describe_ReportsTheCodecResolutionAndAudio()
    {
        var command = Build(Job(ExportFormat.H265, ExportResolution.Resolution720p, ExportAudioChannels.Stereo));

        Assert.Equal("libx265 1280x720 audio=aac (software)", command.Describe());
    }
}
