using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Exporter.Models;

namespace Jellyfin.Plugin.Exporter.Export;

/// <summary>
/// Chooses the hardware acceleration family for this server. The family the
/// operator configured in Jellyfin's own encoding settings is tried first;
/// <c>none</c> (Jellyfin's default) means "no preference", so the selector then
/// probes CUDA, VA-API, QSV and VideoToolbox and uses the first family FFmpeg
/// can actually encode with (capability checks and the real test encode are
/// injected).
///
/// Verification is per EXACT encoder, not per family: FFmpeg advertises
/// encoders the current device cannot run (a UHD 630 lists <c>av1_vaapi</c>
/// and fails with <c>No usable encoding profile found</c>), so every format is
/// tested with the exact <c>&lt;codec&gt;_&lt;family&gt;</c> name on a real
/// one-frame encode, and for VA-API/QSV the configured device is tried first
/// followed by the render nodes that actually exist. A format with no working
/// device falls back to software instead of failing the job.
/// </summary>
public static class HardwareAccelerationSelector
{
    /// <summary>Acceleration families in probing order.</summary>
    public static readonly IReadOnlyList<string> ProbeOrder = new[] { "cuda", "vaapi", "qsv", "videotoolbox" };

    /// <summary>Export formats verified per family, in probing order.</summary>
    public static readonly IReadOnlyList<ExportFormat> FormatOrder =
        new[] { ExportFormat.H264, ExportFormat.H265, ExportFormat.AV1 };

    /// <summary>
    /// Selects a plan.
    /// </summary>
    /// <param name="configuredType">Jellyfin's <c>HardwareAccelerationType</c> as configured, e.g. <c>nvenc</c>.</param>
    /// <param name="supportsHwaccel">IMediaEncoder.SupportsHwaccel.</param>
    /// <param name="supportsEncoder">IMediaEncoder.SupportsEncoder.</param>
    /// <param name="vaapiDevice">Configured VA-API device, when any.</param>
    /// <param name="verifyFamily">Optional real encode check per family (one frame through the hardware encoder).</param>
    /// <param name="verifyEncoder">
    /// Optional real encode check per exact encoder on a device
    /// (<c>(device, encoder)</c>); when given, every format is verified with
    /// the exact encoder name and, for VA-API/QSV, the device fallback runs.
    /// </param>
    /// <param name="renderNodes">
    /// Render nodes that exist on this machine (<c>/dev/dri/renderD*</c>),
    /// used as fallback devices after the configured one. Null means "no
    /// siblings", so only the configured device is probed.
    /// </param>
    /// <param name="qsvDevice">Configured QSV device (<c>EncodingOptions.QsvDevice</c>), when any.</param>
    /// <returns>The plan to encode with; software when nothing is usable.</returns>
    public static HwAccelPlan Select(
        string? configuredType,
        Func<string, bool> supportsHwaccel,
        Func<string, bool> supportsEncoder,
        string? vaapiDevice = null,
        Func<string, bool>? verifyFamily = null,
        Func<string?, string, bool>? verifyEncoder = null,
        IReadOnlyList<string>? renderNodes = null,
        string? qsvDevice = null)
    {
        ArgumentNullException.ThrowIfNull(supportsHwaccel);
        ArgumentNullException.ThrowIfNull(supportsEncoder);

        var candidates = new List<string>();
        var normalized = (configuredType ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized.Length > 0 && normalized != "none")
        {
            var mapped = MapConfiguredType(normalized);
            if (mapped is not null)
            {
                candidates.Add(mapped);
            }
        }

        candidates.AddRange(ProbeOrder.Where(family => !candidates.Contains(family, StringComparer.Ordinal)));

        foreach (var family in candidates)
        {
            if (!SafeSupports(supportsHwaccel, family))
            {
                continue;
            }

            if (!AnyEncoder(supportsEncoder, family))
            {
                continue;
            }

            // FFmpeg lists more hwaccels than the machine can run (a container
            // without a GPU still reports cuda), so the family is only used
            // when a real one-frame encode succeeds.
            if (verifyFamily is not null && !SafeSupports(verifyFamily, family))
            {
                continue;
            }

            if (verifyEncoder is null)
            {
                return new HwAccelPlan(KindOf(family), PlanDevice(family, vaapiDevice, qsvDevice));
            }

            // Verify the EXACT encoder each format will use, on the configured
            // device first and then on render nodes that exist here. A family
            // where no format has a working device is skipped entirely, so the
            // next family (or software) gets its chance.
            var plan = VerifyFormats(family, verifyEncoder, vaapiDevice, qsvDevice, renderNodes);
            if (plan is not null)
            {
                return plan;
            }
        }

        return HwAccelPlan.Software;
    }

    /// <summary>
    /// Maps a Jellyfin acceleration setting onto the FFmpeg family name.
    /// </summary>
    /// <param name="configuredType">Configured value, lower case.</param>
    /// <returns>Family name, or null when the setting is not one the exporter implements.</returns>
    public static string? MapConfiguredType(string configuredType) => configuredType switch
    {
        "nvenc" => "cuda",
        "cuda" => "cuda",
        "vaapi" => "vaapi",
        "qsv" => "qsv",
        "videotoolbox" => "videotoolbox",
        _ => null
    };

    /// <summary>
    /// Encoder suffix FFmpeg uses for a family.
    /// </summary>
    /// <param name="family">Family name.</param>
    /// <returns>Suffix such as <c>nvenc</c>.</returns>
    public static string EncoderSuffix(string family) => family switch
    {
        "cuda" => "nvenc",
        "vaapi" => "vaapi",
        "qsv" => "qsv",
        "videotoolbox" => "videotoolbox",
        _ => throw new ArgumentOutOfRangeException(nameof(family), family, "Unknown acceleration family.")
    };

    /// <summary>
    /// The exact FFmpeg encoder name for a format in a family, e.g.
    /// <c>av1_vaapi</c> — the name the one-frame test encode has to prove.
    /// </summary>
    /// <param name="format">Export format.</param>
    /// <param name="family">Family name.</param>
    /// <returns>Exact encoder name.</returns>
    public static string EncoderName(ExportFormat format, string family)
    {
        var prefix = format switch
        {
            ExportFormat.H264 => "h264",
            ExportFormat.H265 => "hevc",
            _ => "av1"
        };

        return prefix + "_" + EncoderSuffix(family);
    }

    private static HwAccelPlan? VerifyFormats(
        string family,
        Func<string?, string, bool> verifyEncoder,
        string? vaapiDevice,
        string? qsvDevice,
        IReadOnlyList<string>? renderNodes)
    {
        var kind = KindOf(family);
        var devices = DeviceCandidates(family, vaapiDevice, qsvDevice, renderNodes);
        var formats = new Dictionary<ExportFormat, FormatAcceleration>();
        var hardwareFormats = 0;

        foreach (var format in FormatOrder)
        {
            if (family == "videotoolbox" && format == ExportFormat.AV1)
            {
                // VideoToolbox has no AV1 encoder; the command builder sends
                // AV1 in software there, so do not waste a probe on it.
                formats[format] = FormatAcceleration.Software;
                continue;
            }

            var encoder = EncoderName(format, family);
            var resolved = FormatAcceleration.Software;
            foreach (var device in devices)
            {
                if (SafeVerify(verifyEncoder, device, encoder))
                {
                    resolved = new FormatAcceleration(kind, device);
                    hardwareFormats++;
                    break;
                }
            }

            formats[format] = resolved;
        }

        if (hardwareFormats == 0)
        {
            return null;
        }

        return new HwAccelPlan(kind, PlanDevice(family, vaapiDevice, qsvDevice)) { Formats = formats };
    }

    /// <summary>
    /// Devices to probe for a family, in order: the configured device first
    /// (VA-API/QSV), then the render nodes that actually exist on this machine,
    /// sorted. A configured device that no longer exists simply fails fast and
    /// the next node is tried.
    /// </summary>
    private static IReadOnlyList<string?> DeviceCandidates(
        string family,
        string? vaapiDevice,
        string? qsvDevice,
        IReadOnlyList<string>? renderNodes)
    {
        var devices = new List<string?>();
        if (family == "vaapi")
        {
            devices.Add(NormalizeVaapiDevice(vaapiDevice));
        }
        else if (family == "qsv")
        {
            // An empty QsvDevice means "FFmpeg's default adapter", probed as
            // no device flag at all.
            devices.Add(string.IsNullOrWhiteSpace(qsvDevice) ? null : qsvDevice);
        }
        else
        {
            // CUDA and VideoToolbox take no render-node device here.
            return new string?[] { null };
        }

        if (renderNodes is not null)
        {
            foreach (var node in renderNodes.OrderBy(node => node, StringComparer.Ordinal))
            {
                if (!devices.Contains(node))
                {
                    devices.Add(node);
                }
            }
        }

        return devices;
    }

    /// <summary>The family-level device a plan reports (configured device first).</summary>
    private static string? PlanDevice(string family, string? vaapiDevice, string? qsvDevice) => family switch
    {
        "vaapi" => NormalizeVaapiDevice(vaapiDevice),
        "qsv" => string.IsNullOrWhiteSpace(qsvDevice) ? null : qsvDevice,
        _ => null
    };

    private static bool SafeVerify(Func<string?, string, bool> verify, string? device, string encoder)
    {
        try
        {
            return verify(device, encoder);
        }
        catch (Exception)
        {
            // A probing failure means "not usable": never let detection stop
            // an export from running in software.
            return false;
        }
    }

    private static bool AnyEncoder(Func<string, bool> supportsEncoder, string family)
    {
        var suffix = EncoderSuffix(family);
        return SafeSupports(supportsEncoder, "h264_" + suffix)
            || SafeSupports(supportsEncoder, "hevc_" + suffix)
            || SafeSupports(supportsEncoder, "av1_" + suffix);
    }

    private static bool SafeSupports(Func<string, bool> probe, string argument)
    {
        try
        {
            return probe(argument);
        }
        catch (Exception)
        {
            // A probing failure means "not available": never let detection
            // stop an export from running in software.
            return false;
        }
    }

    private static HwAccelKind KindOf(string family) => family switch
    {
        "cuda" => HwAccelKind.Cuda,
        "vaapi" => HwAccelKind.Vaapi,
        "qsv" => HwAccelKind.Qsv,
        "videotoolbox" => HwAccelKind.VideoToolbox,
        _ => HwAccelKind.None
    };

    private static string NormalizeVaapiDevice(string? device) =>
        string.IsNullOrWhiteSpace(device) ? FfmpegCommandBuilder.DefaultVaapiDevice : device!;
}
