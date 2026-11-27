using Jellyfin.Plugin.Exporter.Export;
using Jellyfin.Plugin.Exporter.Models;

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

    // ---- Exact-encoder verification (device, encoder) with an injected prober ----

    private const string D128 = "/dev/dri/renderD128";
    private const string D129 = "/dev/dri/renderD129";

    private static HwAccelPlan SelectVerified(
        string? configured,
        Func<string?, string, bool> verifyEncoder,
        string? vaapiDevice = D128,
        string? qsvDevice = null,
        IReadOnlyList<string>? renderNodes = null) =>
        HardwareAccelerationSelector.Select(
            configured,
            _ => true,
            _ => true,
            vaapiDevice,
            verifyFamily: null,
            verifyEncoder: verifyEncoder,
            renderNodes: renderNodes ?? new[] { D128, D129 },
            qsvDevice: qsvDevice);

    [Fact]
    public void Select_ExactEncoderOk_UsesHardwareOnTheConfiguredDevice()
    {
        // (a) family ok + codec ok -> hardware on the configured device.
        var plan = SelectVerified("vaapi", (_, _) => true);

        Assert.Equal(HwAccelKind.Vaapi, plan.Kind);
        Assert.Equal(D128, plan.DevicePath);
        Assert.Equal(HwAccelKind.Vaapi, plan.ForFormat(ExportFormat.AV1).Kind);
        Assert.Equal(D128, plan.ForFormat(ExportFormat.AV1).DevicePath);
        Assert.Equal(HwAccelKind.Vaapi, plan.ForFormat(ExportFormat.H264).Kind);
    }

    [Fact]
    public void Select_ExactEncoderFailsOnConfiguredDevice_UsesTheSiblingRenderNode()
    {
        // (b) family ok + codec fails on the configured device but passes on a
        // sibling node -> the sibling node.
        var plan = SelectVerified(
            "vaapi",
            (device, encoder) => encoder != "av1_vaapi" || device == D129);

        Assert.Equal(HwAccelKind.Vaapi, plan.Kind);
        Assert.Equal(D128, plan.DevicePath);
        Assert.Equal(D128, plan.ForFormat(ExportFormat.H264).DevicePath);
        Assert.Equal(D128, plan.ForFormat(ExportFormat.H265).DevicePath);
        Assert.Equal(D129, plan.ForFormat(ExportFormat.AV1).DevicePath);
    }

    [Fact]
    public void Select_ExactEncoderFailsEverywhere_FallsBackToSoftware()
    {
        // (c) codec fails everywhere -> software encoder.
        var plan = SelectVerified("vaapi", (_, _) => false);

        Assert.Equal(HwAccelKind.None, plan.Kind);
        Assert.False(plan.IsHardware);
        Assert.Equal(HwAccelKind.None, plan.ForFormat(ExportFormat.AV1).Kind);
    }

    [Fact]
    public void Select_ThrowingEncoderProbe_FallsBackToSoftware()
    {
        // (d) a prober that throws must never stop an export: software.
        var plan = SelectVerified(
            "vaapi",
            (_, _) => throw new InvalidOperationException("ffmpeg missing"));

        Assert.Equal(HwAccelKind.None, plan.Kind);
    }

    [Fact]
    public void Select_Uhd630WithoutAv1Encode_GivesAv1TheArcNodeAndKeepsHevcOnTheConfiguredNode()
    {
        // Regression for the 18 failed AV1 jobs: renderD128 (UHD 630) cannot
        // encode AV1, renderD129 can; h264/hevc work on renderD128, so they
        // must stay there while AV1 moves to its sibling.
        var plan = SelectVerified(
            "vaapi",
            (device, encoder) => encoder != "av1_vaapi" || device == D129);

        Assert.Equal(D128, plan.ForFormat(ExportFormat.H264).DevicePath);
        Assert.Equal(D128, plan.ForFormat(ExportFormat.H265).DevicePath);
        Assert.Equal(D129, plan.ForFormat(ExportFormat.AV1).DevicePath);

        var av1 = FfmpegCommandBuilder.ResolveVideoEncoder(ExportFormat.AV1, plan, svtAv1Available: true);
        Assert.Equal("av1_vaapi", av1.Encoder);
        Assert.Equal(D129, av1.DevicePath);

        var h264 = FfmpegCommandBuilder.ResolveVideoEncoder(ExportFormat.H264, plan, svtAv1Available: true);
        Assert.Equal("h264_vaapi", h264.Encoder);
        Assert.Equal(D128, h264.DevicePath);
    }

    [Fact]
    public void Select_QsvPlan_UsesTheQsvDeviceAndNeverTheVaapiDevice()
    {
        // The configured VaapiDevice is stale (renderD128 is not even mounted
        // in the container); a qsv plan must use QsvDevice.
        var plan = SelectVerified(
            "qsv",
            (_, _) => true,
            vaapiDevice: D128,
            qsvDevice: D129);

        Assert.Equal(HwAccelKind.Qsv, plan.Kind);
        Assert.Equal(D129, plan.DevicePath);
        Assert.Equal(D129, plan.ForFormat(ExportFormat.AV1).DevicePath);

        var av1 = FfmpegCommandBuilder.ResolveVideoEncoder(ExportFormat.AV1, plan, svtAv1Available: true);
        Assert.Equal("av1_qsv", av1.Encoder);
        Assert.Equal(D129, av1.DevicePath);
    }

    [Fact]
    public void Select_SiblingProbing_StaysWithinRenderNodesThatExist()
    {
        // Configured device first, then only nodes that actually exist here:
        // the container mounts renderD129 only, so nothing else is probed.
        var probed = new List<string?>();
        var plan = HardwareAccelerationSelector.Select(
            "vaapi",
            _ => true,
            _ => true,
            D128,
            verifyFamily: null,
            verifyEncoder: (device, encoder) =>
            {
                probed.Add(device);
                return device == D129;
            },
            renderNodes: new[] { D129 },
            qsvDevice: null);

        Assert.Equal(HwAccelKind.Vaapi, plan.Kind);
        Assert.Equal(D129, plan.ForFormat(ExportFormat.H264).DevicePath);
        Assert.Equal(new string?[] { D128, D129, D128, D129, D128, D129 }, probed);
    }

    [Fact]
    public void Select_QsvWithoutAConfiguredDevice_ProbesTheFfmpegDefaultFirst()
    {
        var plan = SelectVerified("qsv", (_, _) => true, vaapiDevice: D128, qsvDevice: string.Empty);

        Assert.Equal(HwAccelKind.Qsv, plan.Kind);
        Assert.Null(plan.DevicePath);
        Assert.Null(plan.ForFormat(ExportFormat.H264).DevicePath);
    }

    [Fact]
    public void Select_NoRenderNodes_OnlyTheConfiguredDeviceIsProbed()
    {
        var probed = new List<string?>();
        var plan = HardwareAccelerationSelector.Select(
            "vaapi",
            _ => true,
            _ => true,
            D128,
            verifyFamily: null,
            verifyEncoder: (device, _) =>
            {
                probed.Add(device);
                return false;
            },
            renderNodes: null,
            qsvDevice: null);

        Assert.Equal(HwAccelKind.None, plan.Kind);
        // Every format was tried on the configured device (three probes), and
        // no node beyond it was ever probed; later families only probe their
        // own no-device encoders.
        Assert.Equal(new string?[] { D128, D128, D128 }, probed.Take(3));
        Assert.DoesNotContain(probed, device => device is not null && device != D128);
    }

    [Theory]
    [InlineData(ExportFormat.H264, "vaapi", "h264_vaapi")]
    [InlineData(ExportFormat.H265, "vaapi", "hevc_vaapi")]
    [InlineData(ExportFormat.AV1, "vaapi", "av1_vaapi")]
    [InlineData(ExportFormat.AV1, "qsv", "av1_qsv")]
    [InlineData(ExportFormat.H265, "cuda", "hevc_nvenc")]
    public void EncoderName_BuildsTheExactEncoderTheCommandWillUse(ExportFormat format, string family, string expected)
    {
        Assert.Equal(expected, HardwareAccelerationSelector.EncoderName(format, family));
    }
}
