using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.Exporter.Export;
using Jellyfin.Plugin.Exporter.Services;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Exporter.Api;

/// <summary>
/// Read-only diagnostics for the export engine: which FFmpeg the plugin
/// resolved from Jellyfin, which acceleration it will use, and — per export
/// format — the exact encoder and device (or the software fallback) the next
/// job of that format will get. Operators and QA use this to confirm hardware
/// acceleration was detected per codec (or that the server correctly fell
/// back to software encoding) without running an export.
/// </summary>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("JellyfinExporter/Engine")]
public class ExporterEngineController : ControllerBase
{
    private readonly IFfmpegLocator? _locator;
    private readonly ITranscodingSettingsSource? _transcoding;

    /// <summary>
    /// Initializes a new instance of the <see cref="ExporterEngineController"/>
    /// class. Used when the plugin's services are not registered yet.
    /// </summary>
    public ExporterEngineController()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ExporterEngineController"/> class.
    /// </summary>
    /// <param name="locator">FFmpeg locator.</param>
    /// <param name="transcoding">Jellyfin's transcoding settings, for the hardware-switch note.</param>
    public ExporterEngineController(IFfmpegLocator locator, ITranscodingSettingsSource? transcoding = null)
    {
        _locator = locator;
        _transcoding = transcoding;
    }

    /// <summary>
    /// Reports the resolved FFmpeg toolchain and acceleration plan.
    /// </summary>
    /// <returns>The engine state, or 503 when FFmpeg cannot be resolved.</returns>
    [HttpGet]
    public ActionResult<EngineInfoResponse> Get()
    {
        if (_locator is null)
        {
            return StatusCode(503, new EngineInfoResponse
            {
                Message = "The export engine is not registered (the plugin's services are unavailable)."
            });
        }

        try
        {
            var location = _locator.Locate();
            var formats = DescribeFormats(location);
            var hardwareCount = formats.Count(format => format.Hardware);

            return new EngineInfoResponse
            {
                EncoderPath = location.EncoderPath,
                ProbePath = location.ProbePath,
                EncoderExists = System.IO.File.Exists(location.EncoderPath),
                ProbeExists = System.IO.File.Exists(location.ProbePath),
                HardwareAcceleration = location.HwAccel.FfmpegName,
                HardwareEncoding = location.HwAccel.IsHardware,
                VaapiDevice = location.HwAccel.Kind == HwAccelKind.Vaapi ? location.HwAccel.DevicePath : null,
                QsvDevice = location.HwAccel.Kind == HwAccelKind.Qsv ? location.HwAccel.DevicePath : null,
                SvtAv1Available = location.SvtAv1Available,
                Formats = formats,
                Message = BuildMessage(location, formats, hardwareCount)
            };
        }
        catch (Exception ex)
        {
            return StatusCode(503, new EngineInfoResponse { Message = ex.Message });
        }
    }

    private static IReadOnlyList<EngineFormatInfo> DescribeFormats(FfmpegLocation location)
    {
        return HardwareAccelerationSelector.FormatOrder
            .Select(format =>
            {
                var choice = FfmpegCommandBuilder.ResolveVideoEncoder(
                    format,
                    location.HwAccel,
                    location.SvtAv1Available);
                return new EngineFormatInfo
                {
                    Format = format.ToString(),
                    Hardware = choice.Hardware,
                    Encoder = choice.Encoder,
                    Device = choice.Hardware ? choice.DevicePath : null
                };
            })
            .ToArray();
    }

    private string BuildMessage(FfmpegLocation location, IReadOnlyList<EngineFormatInfo> formats, int hardwareCount)
    {
        string message;
        if (hardwareCount == formats.Count)
        {
            // Kept verbatim: operators and QA scripts match this sentence.
            message = "Hardware encoding is available and will be used.";
        }
        else if (hardwareCount == 0)
        {
            message = "No usable hardware encoder was detected; exports will use software encoding.";
        }
        else
        {
            var hardware = formats
                .Where(format => format.Hardware)
                .Select(format => string.Concat(
                    format.Format,
                    " (",
                    format.Encoder,
                    string.IsNullOrEmpty(format.Device) ? string.Empty : " on " + format.Device,
                    ")"));
            var software = formats
                .Where(format => !format.Hardware)
                .Select(format => string.Concat(format.Format, " (", format.Encoder, ")"));

            message = string.Concat(
                "Hardware encoding is available and will be used for ",
                string.Join(", ", hardware),
                "; ",
                string.Join(", ", software),
                " will use software encoding because no usable hardware encoder was found.");
        }

        if (hardwareCount > 0 && _transcoding?.Current is { EnableHardwareEncoding: false })
        {
            message += " Jellyfin's transcoding settings disable hardware encoding, so profiles using them will encode in software.";
        }

        return message;
    }
}

/// <summary>
/// Engine diagnostics returned by <see cref="ExporterEngineController"/>.
/// </summary>
public class EngineInfoResponse
{
    /// <summary>Gets or sets the FFmpeg path Jellyfin reports.</summary>
    public string? EncoderPath { get; set; }

    /// <summary>Gets or sets the ffprobe path Jellyfin reports.</summary>
    public string? ProbePath { get; set; }

    /// <summary>Gets or sets a value indicating whether the FFmpeg binary is present.</summary>
    public bool EncoderExists { get; set; }

    /// <summary>Gets or sets a value indicating whether the ffprobe binary is present.</summary>
    public bool ProbeExists { get; set; }

    /// <summary>Gets or sets the acceleration family in use: cuda, vaapi, qsv, videotoolbox or none.</summary>
    public string HardwareAcceleration { get; set; } = "none";

    /// <summary>Gets or sets a value indicating whether a hardware encoder will be used.</summary>
    public bool HardwareEncoding { get; set; }

    /// <summary>Gets or sets the VA-API render device, when VA-API is used.</summary>
    public string? VaapiDevice { get; set; }

    /// <summary>Gets or sets the QSV render device (<c>EncodingOptions.QsvDevice</c>), when QSV is used.</summary>
    public string? QsvDevice { get; set; }

    /// <summary>Gets or sets a value indicating whether libsvtav1 is available for software AV1.</summary>
    public bool SvtAv1Available { get; set; }

    /// <summary>
    /// Gets or sets the per-format decision: the exact encoder every export
    /// format will use and the device it was verified on, or the software
    /// fallback when no device can encode that codec.
    /// </summary>
    public IReadOnlyList<EngineFormatInfo> Formats { get; set; } = Array.Empty<EngineFormatInfo>();

    /// <summary>Gets or sets an operator-facing summary or error.</summary>
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// What one export format will actually encode with.
/// </summary>
public class EngineFormatInfo
{
    /// <summary>Gets or sets the format name: H264, H265 or AV1.</summary>
    public string Format { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether a hardware encoder is used for this format.</summary>
    public bool Hardware { get; set; }

    /// <summary>Gets or sets the exact FFmpeg encoder name, e.g. <c>h264_vaapi</c> or <c>libsvtav1</c>.</summary>
    public string Encoder { get; set; } = string.Empty;

    /// <summary>Gets or sets the render device the encoder runs on, when hardware is used.</summary>
    public string? Device { get; set; }
}
