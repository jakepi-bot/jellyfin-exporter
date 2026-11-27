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
        MediaProbeInfo? probe = null,
        JellyfinTranscodingSettings? jellyfin = null) =>
        FfmpegCommandBuilder.Build(job, probe ?? Probe, "/media/source.mkv", "/exports/temp.partial", plan, svtAv1, jellyfin);

    /// <summary>Plan where H.264/H.265 run on renderD128 and AV1 only on renderD129 (UHD 630 has no AV1 encode).</summary>
    private static HwAccelPlan SplitAv1Plan() => new(HwAccelKind.Vaapi, "/dev/dri/renderD128")
    {
        Formats = new Dictionary<ExportFormat, FormatAcceleration>
        {
            [ExportFormat.H264] = new FormatAcceleration(HwAccelKind.Vaapi, "/dev/dri/renderD128"),
            [ExportFormat.H265] = new FormatAcceleration(HwAccelKind.Vaapi, "/dev/dri/renderD128"),
            [ExportFormat.AV1] = FormatAcceleration.Software
        }
    };

    /// <summary>Plan where AV1 moved to the sibling node that can encode it.</summary>
    private static HwAccelPlan SiblingAv1Plan() => new(HwAccelKind.Vaapi, "/dev/dri/renderD128")
    {
        Formats = new Dictionary<ExportFormat, FormatAcceleration>
        {
            [ExportFormat.H264] = new FormatAcceleration(HwAccelKind.Vaapi, "/dev/dri/renderD128"),
            [ExportFormat.H265] = new FormatAcceleration(HwAccelKind.Vaapi, "/dev/dri/renderD128"),
            [ExportFormat.AV1] = new FormatAcceleration(HwAccelKind.Vaapi, "/dev/dri/renderD129")
        }
    };

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

    // ---- Per-format decisions from the exact-encoder verification ----

    [Fact]
    public void Build_Av1OnASplitPlan_UsesSoftwareWhileH264StaysOnTheConfiguredDevice()
    {
        // The UHD 630 (renderD128) cannot encode AV1: the AV1 job must never
        // see av1_vaapi, while an H264 job on the same plan keeps h264_vaapi.
        var av1 = Build(Job(ExportFormat.AV1), SplitAv1Plan());
        Assert.Contains("-c:v libsvtav1", Arguments(av1));
        Assert.DoesNotContain("av1_vaapi", Arguments(av1));
        Assert.DoesNotContain("-vaapi_device", Arguments(av1));
        Assert.False(av1.HardwareVideoEncoder);

        var h264 = Build(Job(ExportFormat.H264), SplitAv1Plan());
        Assert.Contains("-c:v h264_vaapi", Arguments(h264));
        Assert.Contains("-vaapi_device /dev/dri/renderD128", Arguments(h264));
        Assert.True(h264.HardwareVideoEncoder);
    }

    [Fact]
    public void Build_Av1OnTheSiblingNode_UsesAv1VaapiOnThatNode()
    {
        var command = Build(Job(ExportFormat.AV1), SiblingAv1Plan());

        Assert.Contains("-c:v av1_vaapi", Arguments(command));
        Assert.Contains("-vaapi_device /dev/dri/renderD129", Arguments(command));
        Assert.DoesNotContain("/dev/dri/renderD128", Arguments(command));
        Assert.True(command.HardwareVideoEncoder);
    }

    [Fact]
    public void Build_QsvPlan_PassesTheConfiguredQsvDevice()
    {
        var plan = new HwAccelPlan(HwAccelKind.Qsv, "/dev/dri/renderD129");

        var command = Build(Job(ExportFormat.AV1), plan);

        Assert.Contains("-c:v av1_qsv", Arguments(command));
        Assert.Contains("-qsv_device /dev/dri/renderD129", Arguments(command));
        Assert.DoesNotContain("-vaapi_device", Arguments(command));
    }

    // ---- Jellyfin transcoding settings (UseJellyfinTranscodingSettings) ----

    [Fact]
    public void Build_JellyfinDisablesHardware_ForcesSoftwareWhenTheToggleIsOn()
    {
        var job = Job(ExportFormat.H264);
        Assert.True(job.UseJellyfinTranscodingSettings);

        var command = Build(
            job,
            new HwAccelPlan(HwAccelKind.Vaapi, "/dev/dri/renderD128"),
            jellyfin: new JellyfinTranscodingSettings(EnableHardwareEncoding: false));

        Assert.Contains("-c:v libx264", Arguments(command));
        Assert.False(command.HardwareVideoEncoder);
        Assert.DoesNotContain("-vaapi_device", Arguments(command));
    }

    [Fact]
    public void Build_ProfileToggleOff_KeepsHardwareWhenJellyfinDisablesIt()
    {
        var job = Job(ExportFormat.H264);
        job.UseJellyfinTranscodingSettings = false;

        var command = Build(
            job,
            new HwAccelPlan(HwAccelKind.Vaapi, "/dev/dri/renderD128"),
            jellyfin: new JellyfinTranscodingSettings(EnableHardwareEncoding: false));

        Assert.Contains("-c:v h264_vaapi", Arguments(command));
        Assert.True(command.HardwareVideoEncoder);
    }

    [Fact]
    public void Build_HonoursJellyfinCrfForSoftwareH264AndH265()
    {
        var settings = new JellyfinTranscodingSettings(H264Crf: 21, H265Crf: 30);

        Assert.Contains("-crf 21", Arguments(Build(Job(ExportFormat.H264), jellyfin: settings)));
        Assert.Contains("-crf 30", Arguments(Build(Job(ExportFormat.H265), jellyfin: settings)));
    }

    [Fact]
    public void Build_KeepsThePluginAv1SettingsBecauseJellyfinHasNoAv1Crf()
    {
        var settings = new JellyfinTranscodingSettings(H264Crf: 21, H265Crf: 30, EncoderPreset: "slow");

        var command = Build(Job(ExportFormat.AV1), jellyfin: settings);

        // SVT-AV1 keeps the documented plugin CRF and only maps the preset.
        Assert.Contains("-crf 32", Arguments(command));
        Assert.Contains("-preset 6", Arguments(command));
    }

    [Fact]
    public void Build_AutoPresetKeepsThePluginPreset()
    {
        var settings = new JellyfinTranscodingSettings(EncoderPreset: "auto", H264Crf: 23);

        var command = Build(Job(ExportFormat.H264), jellyfin: settings);

        Assert.Contains("-preset medium", Arguments(command));
        Assert.Contains("-crf 23", Arguments(command));
    }

    [Fact]
    public void Build_ExplicitPresetMapsOntoTheSoftwareEncoders()
    {
        var settings = new JellyfinTranscodingSettings(EncoderPreset: "slow");

        Assert.Contains("-preset slow", Arguments(Build(Job(ExportFormat.H264), jellyfin: settings)));
        Assert.Contains("-preset slow", Arguments(Build(Job(ExportFormat.H265), jellyfin: settings)));
        Assert.Contains("-preset 6", Arguments(Build(Job(ExportFormat.AV1), jellyfin: settings)));
    }

    [Fact]
    public void Build_ExplicitPresetMapsOntoTheHardwarePresetFlag()
    {
        var settings = new JellyfinTranscodingSettings(EncoderPreset: "slow");

        var qsv = Build(
            Job(ExportFormat.H264),
            new HwAccelPlan(HwAccelKind.Qsv, "/dev/dri/renderD129"),
            jellyfin: settings);
        Assert.Contains("-preset slow", Arguments(qsv));

        var cuda = Build(Job(ExportFormat.H264), new HwAccelPlan(HwAccelKind.Cuda), jellyfin: settings);
        Assert.Contains("-preset p3", Arguments(cuda));

        // VA-API has no preset flag; the plugin's quality knob stays.
        var vaapi = Build(
            Job(ExportFormat.H264),
            new HwAccelPlan(HwAccelKind.Vaapi, "/dev/dri/renderD128"),
            jellyfin: settings);
        Assert.Contains("-qp 23", Arguments(vaapi));
        Assert.DoesNotContain("-preset", Arguments(vaapi));
    }

    [Fact]
    public void Build_ThreadCountAppliesToSoftwareEncodingOnly()
    {
        var settings = new JellyfinTranscodingSettings(EncodingThreadCount: 4);

        var software = Build(Job(ExportFormat.H264), jellyfin: settings);
        Assert.Contains("-threads 4", Arguments(software));

        var hardware = Build(
            Job(ExportFormat.H264),
            new HwAccelPlan(HwAccelKind.Vaapi, "/dev/dri/renderD128"),
            jellyfin: settings);
        Assert.DoesNotContain("-threads", Arguments(hardware));

        var unlimited = Build(Job(ExportFormat.H264), jellyfin: new JellyfinTranscodingSettings(EncodingThreadCount: -1));
        Assert.DoesNotContain("-threads", Arguments(unlimited));
    }

    [Fact]
    public void Build_ProfileToggleOff_UsesThePluginDefaults()
    {
        var job = Job(ExportFormat.H264);
        job.UseJellyfinTranscodingSettings = false;

        var command = Build(
            job,
            jellyfin: new JellyfinTranscodingSettings(EncoderPreset: "slow", H264Crf: 21, EncodingThreadCount: 4));

        // Exactly as before the option existed: plugin preset, plugin CRF, no threads.
        Assert.Contains("-preset medium", Arguments(command));
        Assert.Contains("-crf 20", Arguments(command));
        Assert.DoesNotContain("-threads", Arguments(command));
    }
}
