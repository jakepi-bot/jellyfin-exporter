using Jellyfin.Plugin.Exporter.Export;

namespace Jellyfin.Plugin.Exporter.Tests;

public class MediaProbeInfoTests
{
    private const string ProbeJson = """
    {
      "streams": [
        {
          "codec_type": "video",
          "codec_name": "mjpeg",
          "width": 600,
          "height": 600,
          "disposition": { "attached_pic": 1 }
        },
        {
          "codec_type": "video",
          "codec_name": "hevc",
          "width": 3840,
          "height": 1600,
          "duration": "120.500000",
          "disposition": { "attached_pic": 0 }
        },
        {
          "codec_type": "audio",
          "codec_name": "dts",
          "channels": 6,
          "duration": "120.400000",
          "disposition": { "attached_pic": 0 }
        }
      ],
      "format": {
        "duration": "120.600000"
      }
    }
    """;

    [Fact]
    public void FromProbeJson_PicksTheRealVideoStreamAndTheFormatDuration()
    {
        var probe = MediaProbeInfo.FromProbeJson(ProbeJson);

        Assert.Equal(3840, probe.Width);
        Assert.Equal(1600, probe.Height);
        Assert.Equal(120.6, probe.DurationSeconds, 3);
        Assert.True(probe.HasAudio);
        Assert.Equal(6, probe.AudioChannels);
        Assert.Equal("dts", probe.AudioCodec);
    }

    [Fact]
    public void FromProbeJson_UsesTheStreamDurationWhenTheFormatHasNone()
    {
        const string json = """
        {
          "streams": [
            { "codec_type": "video", "width": 1280, "height": 720, "duration": "42.25" }
          ]
        }
        """;

        var probe = MediaProbeInfo.FromProbeJson(json);

        Assert.Equal(42.25, probe.DurationSeconds, 3);
        Assert.False(probe.HasAudio);
        Assert.Null(probe.AudioCodec);
    }

    [Fact]
    public void FromProbeJson_RejectsFilesWithoutVideo()
    {
        const string json = """
        {
          "streams": [ { "codec_type": "audio", "codec_name": "aac", "channels": 2 } ],
          "format": { "duration": "10.0" }
        }
        """;

        Assert.Throws<ExportException>(() => MediaProbeInfo.FromProbeJson(json));
    }
}
