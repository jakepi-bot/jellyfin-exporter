using Jellyfin.Plugin.Exporter.Export;

namespace Jellyfin.Plugin.Exporter.Tests;

public class HardwareAccelerationSelectorTests
{
    private static HwAccelPlan Select(
        string? configured,
        Func<string, bool> hwaccel,
        Func<string, bool> encoder,
        string? device = null,
        Func<string, bool>? verify = null) =>
        HardwareAccelerationSelector.Select(configured, hwaccel, encoder, device, verify);

    [Fact]
    public void Select_UsesTheConfiguredFamilyWhenItWorks()
    {
        var plan = Select("nvenc", name => name == "cuda", name => name == "hevc_nvenc");

        Assert.Equal(HwAccelKind.Cuda, plan.Kind);
        Assert.Equal("cuda", plan.FfmpegName);
        Assert.True(plan.IsHardware);
        Assert.Null(plan.DevicePath);
    }

    [Fact]
    public void Select_FallsBackWhenTheConfiguredFamilyIsNotSupported()
    {
        var plan = Select(
            "nvenc",
            name => name == "vaapi",
            name => name == "h264_vaapi");

        Assert.Equal(HwAccelKind.Vaapi, plan.Kind);
        Assert.Equal(FfmpegCommandBuilder.DefaultVaapiDevice, plan.DevicePath);
    }

    [Fact]
    public void Select_ProbesInOrderWhenTheServerHasNoPreference()
    {
        var probed = new List<string>();
        var plan = Select(
            "none",
            name =>
            {
                probed.Add(name);
                return name == "qsv";
            },
            name => name == "h264_qsv");

        Assert.Equal(HwAccelKind.Qsv, plan.Kind);
        Assert.Equal(new[] { "cuda", "vaapi", "qsv" }, probed);
    }

    [Fact]
    public void Select_ReturnsSoftwareWhenNothingIsSupported()
    {
        var plan = Select(null, _ => false, _ => false);

        Assert.Equal(HwAccelKind.None, plan.Kind);
        Assert.False(plan.IsHardware);
        Assert.Equal("none", plan.FfmpegName);
    }

    [Fact]
    public void Select_RequiresAnEncoderNotJustTheHardwareAccel()
    {
        var plan = Select("vaapi", name => name == "vaapi", _ => false);

        Assert.Equal(HwAccelKind.None, plan.Kind);
    }

    [Fact]
    public void Select_IgnoresUnimplementedServerSettingsAndStillProbes()
    {
        var plan = Select(
            "amf",
            name => name == "videotoolbox",
            name => name == "h264_videotoolbox");

        Assert.Equal(HwAccelKind.VideoToolbox, plan.Kind);
    }

    [Fact]
    public void Select_KeepsTheConfiguredVaapiDevice()
    {
        var plan = Select(
            "vaapi",
            name => name == "vaapi",
            name => name == "hevc_vaapi",
            "/dev/dri/renderD129");

        Assert.Equal("/dev/dri/renderD129", plan.DevicePath);
    }

    [Fact]
    public void Select_SkipsAFamilyWhoseTestEncodeFails()
    {
        var plan = Select(
            "nvenc",
            name => name == "cuda" || name == "vaapi",
            name => name is "h264_nvenc" or "h264_vaapi",
            verify: family => family == "vaapi");

        Assert.Equal(HwAccelKind.Vaapi, plan.Kind);
    }

    [Fact]
    public void Select_FallsBackToSoftwareWhenEveryTestEncodeFails()
    {
        var plan = Select(
            null,
            _ => true,
            _ => true,
            verify: _ => false);

        Assert.Equal(HwAccelKind.None, plan.Kind);
    }

    [Fact]
    public void Select_TreatsAThrowingProbeAsUnavailable()
    {
        var plan = Select(
            "nvenc",
            _ => throw new InvalidOperationException("ffmpeg missing"),
            _ => throw new InvalidOperationException("ffmpeg missing"));

        Assert.Equal(HwAccelKind.None, plan.Kind);
    }

    [Theory]
    [InlineData("nvenc", "cuda")]
    [InlineData("cuda", "cuda")]
    [InlineData("vaapi", "vaapi")]
    [InlineData("qsv", "qsv")]
    [InlineData("videotoolbox", "videotoolbox")]
    [InlineData("none", null)]
    [InlineData("rkmpp", null)]
    public void MapConfiguredType_MapsTheServerSetting(string configured, string? expected)
    {
        Assert.Equal(expected, HardwareAccelerationSelector.MapConfiguredType(configured));
    }

    [Theory]
    [InlineData("cuda", "nvenc")]
    [InlineData("vaapi", "vaapi")]
    [InlineData("qsv", "qsv")]
    [InlineData("videotoolbox", "videotoolbox")]
    public void EncoderSuffix_MatchesFfmpegNaming(string family, string suffix)
    {
        Assert.Equal(suffix, HardwareAccelerationSelector.EncoderSuffix(family));
    }
}
