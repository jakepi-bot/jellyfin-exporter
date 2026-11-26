using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Jellyfin.Plugin.Exporter.Export;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Exporter.Services;

/// <summary>
/// Resolves FFmpeg through Jellyfin's <see cref="IMediaEncoder"/> and detects
/// hardware acceleration once per server process.
/// </summary>
public sealed class MediaEncoderFfmpegLocator : IFfmpegLocator
{
    private const int ProbeTimeoutMilliseconds = 20000;

    private readonly IMediaEncoder _mediaEncoder;
    private readonly IConfigurationManager _configuration;
    private readonly ILogger<MediaEncoderFfmpegLocator> _logger;
    private readonly object _gate = new();
    private FfmpegLocation? _location;

    public MediaEncoderFfmpegLocator(
        IMediaEncoder mediaEncoder,
        IConfigurationManager configuration,
        ILogger<MediaEncoderFfmpegLocator> logger)
    {
        _mediaEncoder = mediaEncoder;
        _configuration = configuration;
        _logger = logger;
    }

    /// <inheritdoc />
    public FfmpegLocation Locate()
    {
        lock (_gate)
        {
            return _location ??= Detect();
        }
    }

    private FfmpegLocation Detect()
    {
        var encoderPath = _mediaEncoder.EncoderPath;
        if (string.IsNullOrWhiteSpace(encoderPath))
        {
            throw new InvalidOperationException(
                "Jellyfin reports no FFmpeg path (IMediaEncoder.EncoderPath is empty), so exports cannot be encoded.");
        }

        var probePath = string.IsNullOrWhiteSpace(_mediaEncoder.ProbePath) ? encoderPath : _mediaEncoder.ProbePath;

        string? configuredType = null;
        string? vaapiDevice = null;
        try
        {
            var options = _configuration.GetEncodingOptions();
            configuredType = options.HardwareAccelerationType.ToString();
            vaapiDevice = options.VaapiDevice;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Jellyfin Exporter could not read the server encoding options; probing hardware acceleration.");
        }

        var plan = HardwareAccelerationSelector.Select(
            configuredType,
            hwaccel => _mediaEncoder.SupportsHwaccel(hwaccel),
            encoder => _mediaEncoder.SupportsEncoder(encoder),
            vaapiDevice,
            family => TestEncode(family, encoderPath, vaapiDevice));

        var svtAv1 = SupportsEncoderSafe("libsvtav1");

        _logger.LogInformation(
            "Jellyfin Exporter will encode with {EncoderPath} ({Acceleration}); server hardware acceleration setting: {Configured}",
            encoderPath,
            plan.IsHardware ? plan.FfmpegName + " hardware encoding" : "software encoding",
            configuredType ?? "unknown");

        return new FfmpegLocation(encoderPath, probePath, plan, svtAv1);
    }

    /// <summary>
    /// Encodes one black frame through the family's hardware encoder. This is
    /// the only reliable check: FFmpeg advertises hwaccels it cannot use on the
    /// current machine (containers without a GPU still list cuda), so trusting
    /// the advertised list would fail every export.
    /// </summary>
    private bool TestEncode(string family, string encoderPath, string? vaapiDevice)
    {
        var suffix = HardwareAccelerationSelector.EncoderSuffix(family);
        var arguments = new List<string>
        {
            "-hide_banner",
            "-nostdin",
            "-loglevel",
            "error",
            "-f",
            "lavfi",
            "-i",
            "color=c=black:s=256x144:r=1:d=1"
        };

        if (family == "vaapi")
        {
            arguments.Add("-vaapi_device");
            arguments.Add(string.IsNullOrWhiteSpace(vaapiDevice) ? FfmpegCommandBuilder.DefaultVaapiDevice : vaapiDevice!);
            arguments.Add("-vf");
            arguments.Add("format=nv12,hwupload");
        }

        arguments.AddRange(new[] { "-frames:v", "1", "-c:v", "h264_" + suffix, "-f", "null", "-" });

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = encoderPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            var stderr = process.StandardError.ReadToEndAsync();
            process.StandardOutput.ReadToEnd();

            if (!process.WaitForExit(ProbeTimeoutMilliseconds))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Killing the {Family} probe process failed.", family);
                }

                _logger.LogDebug("Hardware acceleration probe for {Family} timed out.", family);
                return false;
            }

            if (process.ExitCode == 0)
            {
                return true;
            }

            _logger.LogDebug(
                "Hardware acceleration probe for {Family} failed: {Error}",
                family,
                Truncate(stderr.GetAwaiter().GetResult()));
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Hardware acceleration probe for {Family} could not run.", family);
            return false;
        }
    }

    private bool SupportsEncoderSafe(string encoder)
    {
        try
        {
            return _mediaEncoder.SupportsEncoder(encoder);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Probing encoder {Encoder} failed.", encoder);
            return false;
        }
    }

    private static string Truncate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "(no output)";
        }

        var collapsed = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length <= 300 ? collapsed : collapsed[^300..];
    }
}
