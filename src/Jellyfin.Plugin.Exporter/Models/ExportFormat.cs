namespace Jellyfin.Plugin.Exporter.Models;

/// <summary>
/// Target video codec for an export profile.
/// </summary>
public enum ExportFormat
{
    /// <summary>H.264 / AVC.</summary>
    H264 = 0,

    /// <summary>H.265 / HEVC.</summary>
    H265 = 1,

    /// <summary>AV1.</summary>
    AV1 = 2
}
