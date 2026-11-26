namespace Jellyfin.Plugin.Exporter.Models;

/// <summary>
/// Target output resolution for an export profile.
/// Member names are prefixed because C# identifiers cannot start with a digit;
/// the UI displays 4K / 1080p / 720p / 480p and the FFmpeg layer maps them to
/// 3840x2160, 1920x1080, 1280x720 and 854x480 respectively.
/// </summary>
public enum ExportResolution
{
    /// <summary>3840x2160.</summary>
    Resolution4K = 0,

    /// <summary>1920x1080.</summary>
    Resolution1080p = 1,

    /// <summary>1280x720.</summary>
    Resolution720p = 2,

    /// <summary>854x480.</summary>
    Resolution480p = 3
}
