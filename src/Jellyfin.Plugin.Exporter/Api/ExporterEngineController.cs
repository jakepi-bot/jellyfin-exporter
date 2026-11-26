using System;
using System.IO;
using Jellyfin.Plugin.Exporter.Services;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Exporter.Api;

/// <summary>
/// Read-only diagnostics for the export engine: which FFmpeg the plugin
/// resolved from Jellyfin and which acceleration it will use. Operators and QA
/// use this to confirm hardware acceleration was detected (or that the server
/// correctly fell back to software encoding) without running an export.
/// </summary>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("JellyfinExporter/Engine")]
public class ExporterEngineController : ControllerBase
{
    private readonly IFfmpegLocator? _locator;

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
    public ExporterEngineController(IFfmpegLocator locator)
    {
        _locator = locator;
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
            return new EngineInfoResponse
            {
                EncoderPath = location.EncoderPath,
                ProbePath = location.ProbePath,
                EncoderExists = System.IO.File.Exists(location.EncoderPath),
                ProbeExists = System.IO.File.Exists(location.ProbePath),
                HardwareAcceleration = location.HwAccel.FfmpegName,
                HardwareEncoding = location.HwAccel.IsHardware,
                VaapiDevice = location.HwAccel.DevicePath,
                SvtAv1Available = location.SvtAv1Available,
                Message = location.HwAccel.IsHardware
                    ? "Hardware encoding is available and will be used."
                    : "No usable hardware encoder was detected; exports will use software encoding."
            };
        }
        catch (Exception ex)
        {
            return StatusCode(503, new EngineInfoResponse { Message = ex.Message });
        }
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

    /// <summary>Gets or sets a value indicating whether libsvtav1 is available for software AV1.</summary>
    public bool SvtAv1Available { get; set; }

    /// <summary>Gets or sets an operator-facing summary or error.</summary>
    public string Message { get; set; } = string.Empty;
}
