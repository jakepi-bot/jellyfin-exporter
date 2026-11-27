using Jellyfin.Plugin.Exporter.Export;
using Jellyfin.Plugin.Exporter.Models;
using Xunit;

namespace Jellyfin.Plugin.Exporter.Tests;

/// <summary>
/// Maps Jellyfin's EncoderPreset names onto the values each encoder accepts,
/// and the transcoding-settings helpers the save/plan checks rely on.
/// </summary>
public class JellyfinPresetMapTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("auto")]
    [InlineData("AUTO")]
    [InlineData("  auto  ")]
    [InlineData("turbo")]
    public void IndexOf_AutoOrUnknownMeansPluginDefault(string? preset)
    {
        Assert.Equal(-1, JellyfinPresetMap.IndexOf(preset));
        Assert.Null(JellyfinPresetMap.X26xPreset(preset));
        Assert.Null(JellyfinPresetMap.SvtPreset(preset));
        Assert.Null(JellyfinPresetMap.HardwarePreset(HwAccelKind.Qsv, preset));
    }

    [Theory]
    [InlineData("slow")]
    [InlineData("Slow")]
    [InlineData(" SLOW ")]
    public void IndexOf_IsCaseInsensitiveAndTrimmed(string preset)
    {
        Assert.True(JellyfinPresetMap.IndexOf(preset) >= 0);
    }

    [Fact]
    public void SoftwareEncoders_TakeTheJellyfinNameOrTheirOwnScale()
    {
        // x264/x265 share Jellyfin's names verbatim.
        Assert.Equal("slow", JellyfinPresetMap.X26xPreset("slow"));
        Assert.Equal("placebo", JellyfinPresetMap.X26xPreset("placebo"));

        // libsvtav1 takes 0 (slowest) .. 13 (fastest); slow is 6, medium is
        // the plugin default 8.
        Assert.Equal("6", JellyfinPresetMap.SvtPreset("slow"));
        Assert.Equal("8", JellyfinPresetMap.SvtPreset("medium"));
        Assert.Equal("13", JellyfinPresetMap.SvtPreset("ultrafast"));
        Assert.Equal("0", JellyfinPresetMap.SvtPreset("placebo"));

        // libaom-av1 takes -cpu-used; medium matches the plugin default 8.
        Assert.Equal("4", JellyfinPresetMap.AomCpuUsed("slower"));
        Assert.Equal("8", JellyfinPresetMap.AomCpuUsed("medium"));
        Assert.Equal("0", JellyfinPresetMap.AomCpuUsed("placebo"));
    }

    [Fact]
    public void HardwareEncoders_TakeTheirOwnPresetScale()
    {
        // NVENC: p1 (slowest) .. p7 (fastest).
        Assert.Equal("p3", JellyfinPresetMap.HardwarePreset(HwAccelKind.Cuda, "slow"));
        Assert.Equal("p4", JellyfinPresetMap.HardwarePreset(HwAccelKind.Cuda, "medium"));
        Assert.Equal("p7", JellyfinPresetMap.HardwarePreset(HwAccelKind.Cuda, "ultrafast"));

        // QSV only knows the x264-style names between veryfast and veryslow.
        Assert.Equal("slow", JellyfinPresetMap.HardwarePreset(HwAccelKind.Qsv, "slow"));
        Assert.Equal("veryslow", JellyfinPresetMap.HardwarePreset(HwAccelKind.Qsv, "placebo"));
        Assert.Equal("veryfast", JellyfinPresetMap.HardwarePreset(HwAccelKind.Qsv, "ultrafast"));

        // VA-API and VideoToolbox have no preset flag at all.
        Assert.Null(JellyfinPresetMap.HardwarePreset(HwAccelKind.Vaapi, "slow"));
        Assert.Null(JellyfinPresetMap.HardwarePreset(HwAccelKind.VideoToolbox, "slow"));
    }

    [Fact]
    public void HonoursPreset_IsFalseForAutoAndEmpty()
    {
        Assert.False(new JellyfinTranscodingSettings(EncoderPreset: "auto").HonoursPreset);
        Assert.False(new JellyfinTranscodingSettings(EncoderPreset: string.Empty).HonoursPreset);
        Assert.True(new JellyfinTranscodingSettings(EncoderPreset: "slow").HonoursPreset);
    }

    [Theory]
    [InlineData(ExportFormat.H264, true, true)]
    [InlineData(ExportFormat.H265, true, false)]
    [InlineData(ExportFormat.AV1, true, false)]
    [InlineData(ExportFormat.H264, false, true)]
    public void Allows_FollowsTheJellyfinCodecSwitches(
        ExportFormat format,
        bool allowHevc,
        bool allowAv1)
    {
        var settings = new JellyfinTranscodingSettings(
            AllowHevcEncoding: allowHevc,
            AllowAv1Encoding: allowAv1);

        var expected = format switch
        {
            ExportFormat.H265 => allowHevc,
            ExportFormat.AV1 => allowAv1,
            _ => true
        };

        Assert.Equal(expected, settings.Allows(format));
    }
}
