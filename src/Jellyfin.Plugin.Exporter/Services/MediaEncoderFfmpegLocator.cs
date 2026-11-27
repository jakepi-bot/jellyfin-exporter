using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.Exporter.Export;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Exporter.Services;

/// <summary>
/// Resolves FFmpeg through Jellyfin's <see cref="IMediaEncoder"/> and detects
/// hardware acceleration once per server process. Verification is per exact
/// encoder (the one the command builder will use) on a specific device, and
/// the result is cached per (device, encoder) so repeated detection is free.
/// </summary>
public sealed class MediaEncoderFfmpegLocator : IFfmpegLocator
{
    private const int ProbeTimeoutMilliseconds = 20000;

    private readonly IMediaEncoder _mediaEncoder;
    private readonly IConfigurationManager _configuration;
    private readonly ILogger<MediaEncoderFfmpegLocator> _logger;
    private readonly EncoderProbeCache _probeCache = new();
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
        string? qsvDevice = null;
        try
        {
            var options = _configuration.GetEncodingOptions();
            configuredType = options.HardwareAccelerationType.ToString();
            vaapiDevice = options.VaapiDevice;
            qsvDevice = options.QsvDevice;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Jellyfin Exporter could not read the server encoding options; probing hardware acceleration.");
        }

        var renderNodes = EnumerateRenderNodes();

        // The plan is decided per EXACT encoder: every format gets a real
        // one-frame test with the name the command builder will use
        // (av1_vaapi, hevc_qsv, ...), on the configured device first and then
        // on render nodes that exist here. A format with no working device
        // falls back to software in the plan.
        var plan = HardwareAccelerationSelector.Select(
            configuredType,
            hwaccel => _mediaEncoder.SupportsHwaccel(hwaccel),
            encoder => _mediaEncoder.SupportsEncoder(encoder),
            vaapiDevice,
            verifyFamily: null,
            verifyEncoder: (device, encoder) => _probeCache.Get(
                device,
                encoder,
                (probeDevice, probeEncoder) => TestEncode(probeEncoder, probeDevice, encoderPath)),
            renderNodes: renderNodes,
            qsvDevice: qsvDevice);

        var svtAv1 = SupportsEncoderSafe("libsvtav1");

        _logger.LogInformation(
            "Jellyfin Exporter will encode with {EncoderPath} ({Acceleration}); server hardware acceleration setting: {Configured}",
            encoderPath,
            plan.IsHardware ? plan.FfmpegName + " hardware encoding" : "software encoding",
            configuredType ?? "unknown");
        _logger.LogInformation(
            "Jellyfin Exporter per-format encoders: {Detail}",
            DescribeFormats(plan, svtAv1));

        return new FfmpegLocation(encoderPath, probePath, plan, svtAv1);
    }

    private static string DescribeFormats(HwAccelPlan plan, bool svtAv1)
    {
        var parts = new List<string>();
        foreach (var format in HardwareAccelerationSelector.FormatOrder)
        {
            var choice = FfmpegCommandBuilder.ResolveVideoEncoder(format, plan, svtAv1);
            parts.Add(string.Concat(
                format.ToString(),
                ": ",
                choice.Encoder,
                choice.Hardware && !string.IsNullOrEmpty(choice.DevicePath) ? " (" + choice.DevicePath + ")" : string.Empty));
        }

        return string.Join("; ", parts);
    }

    /// <summary>
    /// The render nodes present on this machine (<c>/dev/dri/renderD*</c>,
    /// sorted). Used as sibling devices when the configured one cannot encode
    /// a codec; a container that only mounts one node offers only that node.
    /// </summary>
    private static IReadOnlyList<string> EnumerateRenderNodes()
    {
        try
        {
            if (!Directory.Exists("/dev/dri"))
            {
                return Array.Empty<string>();
            }

            var nodes = Directory.GetFiles("/dev/dri", "renderD*", SearchOption.TopDirectoryOnly);
            Array.Sort(nodes, StringComparer.Ordinal);
            return nodes;
        }
        catch (Exception)
        {
            // No sibling probing: the configured device alone decides.
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Encodes one black frame through the exact encoder on the given device.
    /// This is the only reliable check: FFmpeg advertises hwaccels and
    /// encoders the current device cannot run (a UHD 630 lists
    /// <c>av1_vaapi</c> and fails with <c>No usable encoding profile found</c>),
    /// so trusting the advertised list would fail every export.
    /// </summary>
    /// <param name="encoder">Exact encoder name, e.g. <c>av1_vaapi</c>.</param>
    /// <param name="device">Render device for VA-API/QSV, when the encoder takes one.</param>
    /// <param name="encoderPath">Path of the FFmpeg binary.</param>
    /// <returns>Whether the one-frame encode succeeded.</returns>
    private bool TestEncode(string encoder, string? device, string encoderPath)
    {
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

        if (encoder.EndsWith("_vaapi", StringComparison.Ordinal))
        {
            arguments.Add("-vaapi_device");
            arguments.Add(string.IsNullOrWhiteSpace(device) ? FfmpegCommandBuilder.DefaultVaapiDevice : device!);
            arguments.Add("-vf");
            arguments.Add("format=nv12,hwupload");
        }
        else if (encoder.EndsWith("_qsv", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(device))
        {
            // EncodingOptions.QsvDevice: without -qsv_device FFmpeg picks its
            // default adapter, which is not necessarily the configured one.
            arguments.Add("-qsv_device");
            arguments.Add(device!);
        }

        arguments.AddRange(new[] { "-frames:v", "1", "-c:v", encoder, "-f", "null", "-" });

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
                    _logger.LogDebug(ex, "Killing the {Encoder} probe process failed.", encoder);
                }

                _logger.LogDebug("Hardware encoder probe for {Encoder} on {Device} timed out.", encoder, device ?? "(default)");
                return false;
            }

            if (process.ExitCode == 0)
            {
                return true;
            }

            _logger.LogDebug(
                "Hardware encoder probe for {Encoder} on {Device} failed: {Error}",
                encoder,
                device ?? "(default)",
                Truncate(stderr.GetAwaiter().GetResult()));
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Hardware encoder probe for {Encoder} could not run.", encoder);
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
