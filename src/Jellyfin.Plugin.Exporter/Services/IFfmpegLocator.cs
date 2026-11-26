using System;
using Jellyfin.Plugin.Exporter.Export;

namespace Jellyfin.Plugin.Exporter.Services;

/// <summary>
/// Where FFmpeg lives and how this server can accelerate it. Resolved once per
/// server process (capability probing spawns FFmpeg) and shared by every job.
/// </summary>
/// <param name="EncoderPath">Absolute path of the FFmpeg binary.</param>
/// <param name="ProbePath">Absolute path of the ffprobe binary.</param>
/// <param name="HwAccel">Hardware acceleration plan.</param>
/// <param name="SvtAv1Available">Whether <c>libsvtav1</c> can be used for software AV1.</param>
public sealed record FfmpegLocation(string EncoderPath, string ProbePath, HwAccelPlan HwAccel, bool SvtAv1Available);

/// <summary>
/// Resolves Jellyfin's own FFmpeg toolchain. The plugin never bundles or
/// hardcodes an FFmpeg path.
/// </summary>
public interface IFfmpegLocator
{
    /// <summary>
    /// Gets the toolchain for this server.
    /// </summary>
    /// <returns>FFmpeg paths plus the acceleration plan.</returns>
    /// <exception cref="InvalidOperationException">Thrown when Jellyfin has no FFmpeg path configured.</exception>
    FfmpegLocation Locate();
}
