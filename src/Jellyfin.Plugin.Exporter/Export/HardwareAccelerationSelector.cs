using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.Exporter.Export;

/// <summary>
/// Chooses the hardware acceleration family for this server. The family the
/// operator configured in Jellyfin's own encoding settings is tried first;
/// <c>none</c> (Jellyfin's default) means "no preference", so the selector then
/// probes CUDA, VA-API, QSV and VideoToolbox and uses the first family FFmpeg
/// can actually encode with (capability checks and the real test encode are
/// injected).
/// </summary>
public static class HardwareAccelerationSelector
{
    /// <summary>Acceleration families in probing order.</summary>
    public static readonly IReadOnlyList<string> ProbeOrder = new[] { "cuda", "vaapi", "qsv", "videotoolbox" };

    /// <summary>
    /// Selects a plan.
    /// </summary>
    /// <param name="configuredType">Jellyfin's <c>HardwareAccelerationType</c> as configured, e.g. <c>nvenc</c>.</param>
    /// <param name="supportsHwaccel">IMediaEncoder.SupportsHwaccel.</param>
    /// <param name="supportsEncoder">IMediaEncoder.SupportsEncoder.</param>
    /// <param name="vaapiDevice">Configured VA-API device, when any.</param>
    /// <param name="verifyFamily">Optional real encode check per family (one frame through the hardware encoder).</param>
    /// <returns>The plan to encode with; software when nothing is usable.</returns>
    public static HwAccelPlan Select(
        string? configuredType,
        Func<string, bool> supportsHwaccel,
        Func<string, bool> supportsEncoder,
        string? vaapiDevice = null,
        Func<string, bool>? verifyFamily = null)
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

            return new HwAccelPlan(KindOf(family), family == "vaapi" ? NormalizeVaapiDevice(vaapiDevice) : null);
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
