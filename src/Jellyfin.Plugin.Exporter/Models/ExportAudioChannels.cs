namespace Jellyfin.Plugin.Exporter.Models;

/// <summary>
/// Audio handling for an export profile. Anything other than
/// <see cref="Original"/> is re-encoded to AAC at the requested channel count.
/// </summary>
public enum ExportAudioChannels
{
    /// <summary>Copy the source audio stream untouched.</summary>
    Original = 0,

    /// <summary>Transcode to 6-channel (5.1) AAC.</summary>
    Surround51 = 1,

    /// <summary>Transcode to 2-channel (stereo) AAC.</summary>
    Stereo = 2
}
