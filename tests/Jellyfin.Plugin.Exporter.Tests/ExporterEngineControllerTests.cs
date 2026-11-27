using Jellyfin.Plugin.Exporter.Api;
using Jellyfin.Plugin.Exporter.Export;
using Jellyfin.Plugin.Exporter.Models;
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
        Assert.Equal(File.Exists(TestMedia.FfmpegPath), body.EncoderExists);
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

    [Fact]
    public void Get_ReportsPerFormatEncodersAndTheSoftwareFallback()
    {
        // AV1 cannot be encoded on the configured device: the report must say
        // so instead of claiming hardware encoding is available wholesale.
        var plan = new HwAccelPlan(HwAccelKind.Vaapi, "/dev/dri/renderD128")
        {
            Formats = new Dictionary<ExportFormat, FormatAcceleration>
            {
                [ExportFormat.H264] = new FormatAcceleration(HwAccelKind.Vaapi, "/dev/dri/renderD128"),
                [ExportFormat.H265] = new FormatAcceleration(HwAccelKind.Vaapi, "/dev/dri/renderD128"),
                [ExportFormat.AV1] = FormatAcceleration.Software
            }
        };
        var locator = new FixedLocator(new FfmpegLocation(
            TestMedia.FfmpegPath,
            TestMedia.FfprobePath,
            plan,
            SvtAv1Available: true));

        var body = Assert.IsType<EngineInfoResponse>(
            new ExporterEngineController(locator).Get().Value);

        Assert.Equal(3, body.Formats.Count);

        var h264 = body.Formats.Single(format => format.Format == "H264");
        Assert.True(h264.Hardware);
        Assert.Equal("h264_vaapi", h264.Encoder);
        Assert.Equal("/dev/dri/renderD128", h264.Device);

        var av1 = body.Formats.Single(format => format.Format == "AV1");
        Assert.False(av1.Hardware);
        Assert.Equal("libsvtav1", av1.Encoder);
        Assert.Null(av1.Device);

        Assert.Equal("vaapi", body.HardwareAcceleration);
        Assert.True(body.HardwareEncoding);
        Assert.Contains("AV1 (libsvtav1)", body.Message);
        Assert.Contains("will use software encoding", body.Message);
    }

    [Fact]
    public void Get_KeepsTheLegacyMessageWhenEveryFormatHasHardware()
    {
        var plan = new HwAccelPlan(HwAccelKind.Qsv, "/dev/dri/renderD129");
        var locator = new FixedLocator(new FfmpegLocation(
            TestMedia.FfmpegPath,
            TestMedia.FfprobePath,
            plan,
            SvtAv1Available: true));

        var body = Assert.IsType<EngineInfoResponse>(
            new ExporterEngineController(locator).Get().Value);

        Assert.All(body.Formats, format => Assert.True(format.Hardware));
        Assert.Equal("Hardware encoding is available and will be used.", body.Message);
    }

    private sealed class FixedLocator : IFfmpegLocator
    {
        private readonly FfmpegLocation _location;

        public FixedLocator(FfmpegLocation location) => _location = location;

        public FfmpegLocation Locate() => _location;
    }
}
