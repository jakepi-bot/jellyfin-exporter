using Jellyfin.Plugin.Exporter.Api;
using Jellyfin.Plugin.Exporter.Export;
using Jellyfin.Plugin.Exporter.Services;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Exporter.Tests;

public class ExporterEngineControllerTests
{
    [Fact]
    public void Get_ReportsTheResolvedToolchain()
    {
        var locator = new FixedLocator(new FfmpegLocation(
            TestMedia.FfmpegPath,
            TestMedia.FfprobePath,
            new HwAccelPlan(HwAccelKind.Vaapi, "/dev/dri/renderD128"),
            SvtAv1Available: true));

        var result = new ExporterEngineController(locator).Get();

        Assert.Null(result.Result);
        var body = Assert.IsType<EngineInfoResponse>(result.Value);
        Assert.Equal(TestMedia.FfmpegPath, body.EncoderPath);
        Assert.Equal("vaapi", body.HardwareAcceleration);
        Assert.True(body.HardwareEncoding);
        Assert.Equal("/dev/dri/renderD128", body.VaapiDevice);
        Assert.True(body.SvtAv1Available);
        Assert.True(body.EncoderExists);
    }

    [Fact]
    public void Get_ReportsSoftwareEncodingWhenNoHardwareIsAvailable()
    {
        var locator = new FixedLocator(new FfmpegLocation(
            TestMedia.FfmpegPath,
            TestMedia.FfprobePath,
            HwAccelPlan.Software,
            SvtAv1Available: false));

        var result = new ExporterEngineController(locator).Get();

        var body = Assert.IsType<EngineInfoResponse>(result.Value);
        Assert.Equal("none", body.HardwareAcceleration);
        Assert.False(body.HardwareEncoding);
        Assert.Contains("software encoding", body.Message);
    }

    [Fact]
    public void Get_ReturnsServiceUnavailableWhenThePluginServicesAreMissing()
    {
        var result = new ExporterEngineController().Get();

        var unavailable = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(503, unavailable.StatusCode);
        var body = Assert.IsType<EngineInfoResponse>(unavailable.Value);
        Assert.Contains("not registered", body.Message);
    }

    private sealed class FixedLocator : IFfmpegLocator
    {
        private readonly FfmpegLocation _location;

        public FixedLocator(FfmpegLocation location) => _location = location;

        public FfmpegLocation Locate() => _location;
    }
}
