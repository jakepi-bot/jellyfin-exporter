using System;

namespace Jellyfin.Plugin.Exporter.Export;

/// <summary>
/// Maps Jellyfin's <c>EncoderPreset</c> names onto the preset values each
/// encoder actually accepts. Jellyfin's list (from <c>auto</c> down to
/// <c>placebo</c>) matches the x264 naming; every other encoder needs its own
/// translation. Unknown names return null so the caller keeps the plugin
/// default instead of handing FFmpeg an invalid preset.
/// </summary>
public static class JellyfinPresetMap
{
    // Jellyfin's preset names ordered slowest (best quality) to fastest.
    private static readonly string[] Order =
        { "placebo", "veryslow", "slower", "slow", "medium", "fast", "faster", "veryfast", "superfast", "ultrafast" };

    // libsvtav1 takes 0 (slowest) .. 13 (fastest); the plugin default is 8.
    private static readonly string[] SvtAv1 = { "0", "2", "4", "6", "8", "10", "11", "12", "13", "13" };

    // libaom-av1 takes -cpu-used 0 (slowest) .. 8 (fastest); the plugin default is 8.
    private static readonly string[] AomCpuUsedByIndex = { "0", "2", "4", "6", "8", "8", "8", "8", "8", "8" };

    // NVENC takes p1 (slowest) .. p7 (fastest); the plugin default is p5.
    private static readonly string[] Nvenc = { "p1", "p1", "p2", "p3", "p4", "p5", "p6", "p6", "p7", "p7" };

    // QSV accepts the x264-style names between veryfast and veryslow only.
    private static readonly string[] Qsv =
        { "veryslow", "veryslow", "slower", "slow", "medium", "fast", "faster", "veryfast", "veryfast", "veryfast" };

    /// <summary>
    /// Position of a Jellyfin preset in the slowest-to-fastest order.
    /// </summary>
    /// <param name="preset">Preset name, e.g. <c>slow</c>; null, empty, <c>auto</c> or an unknown name return -1.</param>
    /// <returns>Index into the order table, or -1 when the plugin default applies.</returns>
    public static int IndexOf(string? preset)
    {
        if (string.IsNullOrWhiteSpace(preset))
        {
            return -1;
        }

        var name = preset.Trim();
        if (string.Equals(name, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return -1;
        }

        for (var index = 0; index < Order.Length; index++)
        {
            if (string.Equals(Order[index], name, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>
    /// Preset value for the software x264/x265 encoders (the names are shared).
    /// </summary>
    /// <param name="preset">Jellyfin preset name.</param>
    /// <returns>The preset name, or null when the plugin default applies.</returns>
    public static string? X26xPreset(string? preset) => IndexOf(preset) is int index and >= 0 ? Order[index] : null;

    /// <summary>
    /// <c>-preset</c> value for libsvtav1 (a number, 0-13).
    /// </summary>
    /// <param name="preset">Jellyfin preset name.</param>
    /// <returns>The numeric preset, or null when the plugin default (8) applies.</returns>
    public static string? SvtPreset(string? preset) => IndexOf(preset) is int index and >= 0 ? SvtAv1[index] : null;

    /// <summary>
    /// <c>-cpu-used</c> value for libaom-av1 (0-8).
    /// </summary>
    /// <param name="preset">Jellyfin preset name.</param>
    /// <returns>The cpu-used value, or null when the plugin default (8) applies.</returns>
    public static string? AomCpuUsed(string? preset) => IndexOf(preset) is int index and >= 0 ? AomCpuUsedByIndex[index] : null;

    /// <summary>
    /// Preset value for a hardware encoder, when that encoder has a preset flag.
    /// </summary>
    /// <param name="kind">Acceleration family.</param>
    /// <param name="preset">Jellyfin preset name.</param>
    /// <returns>The preset value, or null when the plugin default (or no preset flag, for VA-API/VideoToolbox) applies.</returns>
    public static string? HardwarePreset(HwAccelKind kind, string? preset)
    {
        var index = IndexOf(preset);
        if (index < 0)
        {
            return null;
        }

        return kind switch
        {
            HwAccelKind.Cuda => Nvenc[index],
            HwAccelKind.Qsv => Qsv[index],
            _ => null
        };
    }
}
