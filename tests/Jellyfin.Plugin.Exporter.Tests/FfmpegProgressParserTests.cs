using Jellyfin.Plugin.Exporter.Export;

namespace Jellyfin.Plugin.Exporter.Tests;

public class FfmpegProgressParserTests
{
    [Theory]
    [InlineData("out_time_us=1500000", 3d, 50d)]
    [InlineData("out_time_ms=1500000", 3d, 50d)]
    [InlineData("out_time=00:00:01.500000", 3d, 50d)]
    [InlineData("out_time_us=2000000", 2d, 99.9d)]
    [InlineData("out_time_us=0", 10d, 0d)]
    public void ParsePercent_ConvertsPositionsToPercentages(string line, double duration, double expected)
    {
        Assert.Equal(expected, FfmpegProgressParser.ParsePercent(line, duration));
    }

    [Theory]
    [InlineData("progress=continue")]
    [InlineData("frame=12")]
    [InlineData("speed=1.2x")]
    [InlineData("")]
    [InlineData(null)]
    public void ParsePercent_IgnoresLinesWithoutAPosition(string? line)
    {
        Assert.Null(FfmpegProgressParser.ParsePercent(line, 60));
    }

    [Fact]
    public void ParsePercent_IsUnavailableWhenTheDurationIsUnknown()
    {
        Assert.Null(FfmpegProgressParser.ParsePercent("out_time_us=1500000", 0));
    }

    [Fact]
    public void TryParseSeconds_HandlesMicrosecondsAndClockFormat()
    {
        Assert.True(FfmpegProgressParser.TryParseSeconds("out_time_us=2500000", out var micros));
        Assert.Equal(2.5, micros, 3);

        Assert.True(FfmpegProgressParser.TryParseSeconds("out_time=00:01:02.500000", out var clock));
        Assert.Equal(62.5, clock, 3);

        Assert.False(FfmpegProgressParser.TryParseSeconds("progress=end", out _));
    }
}
