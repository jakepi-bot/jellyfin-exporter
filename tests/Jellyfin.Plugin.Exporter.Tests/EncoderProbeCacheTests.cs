using System;
using Jellyfin.Plugin.Exporter.Services;
using Xunit;

namespace Jellyfin.Plugin.Exporter.Tests;

public class EncoderProbeCacheTests
{
    [Fact]
    public void Get_CachesPerDeviceAndEncoder()
    {
        var cache = new EncoderProbeCache();
        var calls = 0;
        bool Probe(string? _, string __)
        {
            calls++;
            return true;
        }

        Assert.True(cache.Get("/dev/dri/renderD128", "h264_vaapi", Probe));
        Assert.True(cache.Get("/dev/dri/renderD128", "h264_vaapi", Probe));
        Assert.Equal(1, calls);

        // A different device or encoder is a different question.
        Assert.True(cache.Get("/dev/dri/renderD129", "h264_vaapi", Probe));
        Assert.True(cache.Get("/dev/dri/renderD128", "hevc_vaapi", Probe));
        Assert.Equal(3, calls);

        // The cached answer is returned even with a prober that would fail.
        Assert.True(cache.Get("/dev/dri/renderD128", "h264_vaapi", (_, _) => throw new InvalidOperationException()));
        Assert.Equal(3, calls);
    }

    [Fact]
    public void Get_TreatsNoDeviceAndAnEmptyDeviceAsTheSameKey()
    {
        var cache = new EncoderProbeCache();
        var calls = 0;
        bool Probe(string? _, string __)
        {
            calls++;
            return false;
        }

        cache.Get(null, "h264_qsv", Probe);
        cache.Get(null, "h264_qsv", Probe);
        cache.Get(string.Empty, "h264_qsv", Probe);

        Assert.Equal(1, calls);
    }

    [Fact]
    public void Get_PropagatesProbeExceptions()
    {
        var cache = new EncoderProbeCache();

        Assert.Throws<InvalidOperationException>(
            () => cache.Get("/dev/dri/renderD128", "av1_vaapi", (_, _) => throw new InvalidOperationException("boom")));
    }
}
