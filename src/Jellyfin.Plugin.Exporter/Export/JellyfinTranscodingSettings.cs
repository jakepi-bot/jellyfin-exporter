using System;
using Jellyfin.Plugin.Exporter.Models;

namespace Jellyfin.Plugin.Exporter.Export;

/// <summary>
/// The subset of Jellyfin's <c>EncodingOptions</c> the exporter honours when a
/// profile's <c>UseJellyfinTranscodingSettings</c> toggle is on. A plain POCO
/// so the command builder and the validators stay free of server types.
/// </summary>
/// <param name="EnableHardwareEncoding">Jellyfin's hardware-encoding switch; false forces software encoders.</param>
/// <param name="AllowHevcEncoding">Whether Jellyfin allows HEVC encoding (blocks H265 profiles).</param>
/// <param name="AllowAv1Encoding">Whether Jellyfin allows AV1 encoding (blocks AV1 profiles).</param>
/// <param name="EncoderPreset">Jellyfin's encoder preset name; <c>auto</c> means "use the plugin default".</param>
/// <param name="H264Crf">CRF for software H.264.</param>
/// <param name="H265Crf">CRF for software H.265.</param>
/// <param name="EncodingThreadCount">Thread count for software encoding; values &lt;= 0 mean "let FFmpeg decide".</param>
public sealed record JellyfinTranscodingSettings(
    bool EnableHardwareEncoding = true,
    bool AllowHevcEncoding = true,
    bool AllowAv1Encoding = true,
    string EncoderPreset = "auto",
    int H264Crf = 23,
    int H265Crf = 28,
    int EncodingThreadCount = -1)
{
    /// <summary>
    /// Gets the settings to use when Jellyfin's encoding options cannot be
    /// read: Jellyfin's own defaults, which amount to plugin defaults for the
    /// preset (auto) and the documented CRFs (23/28).
    /// </summary>
    public static JellyfinTranscodingSettings Default { get; } = new();

    /// <summary>
    /// Gets a value indicating whether an explicit encoder preset should be
    /// applied. <c>auto</c> (Jellyfin's default) means "plugin default".
    /// </summary>
    public bool HonoursPreset =>
        !string.IsNullOrWhiteSpace(EncoderPreset) &&
        !string.Equals(EncoderPreset.Trim(), "auto", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether Jellyfin's settings allow exporting with the given format.
    /// </summary>
    /// <param name="format">Profile format.</param>
    /// <returns>False when <c>AllowHevcEncoding</c>/<c>AllowAv1Encoding</c> block it.</returns>
    public bool Allows(ExportFormat format) => format switch
    {
        ExportFormat.H265 => AllowHevcEncoding,
        ExportFormat.AV1 => AllowAv1Encoding,
        _ => true
    };
}
