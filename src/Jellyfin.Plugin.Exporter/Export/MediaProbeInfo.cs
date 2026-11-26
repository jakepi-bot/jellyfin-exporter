using System;
using System.Globalization;
using System.Text.Json;

namespace Jellyfin.Plugin.Exporter.Export;

/// <summary>
/// The subset of an ffprobe report the exporter needs: the first real video
/// stream (cover art is not a video stream), the duration for progress, and
/// whether audio has to be mapped.
/// </summary>
public sealed record MediaProbeInfo
{
    /// <summary>Gets the source video codec name, e.g. <c>h264</c>.</summary>
    public string? VideoCodec { get; init; }

    /// <summary>Gets the source video width in pixels.</summary>
    public int Width { get; init; }

    /// <summary>Gets the source video height in pixels.</summary>
    public int Height { get; init; }

    /// <summary>Gets the source duration in seconds; 0 when unknown.</summary>
    public double DurationSeconds { get; init; }

    /// <summary>Gets a value indicating whether the source has an audio stream.</summary>
    public bool HasAudio { get; init; }

    /// <summary>Gets the source audio channel count; 0 when unknown.</summary>
    public int AudioChannels { get; init; }

    /// <summary>Gets the source audio codec name, e.g. <c>aac</c>.</summary>
    public string? AudioCodec { get; init; }

    /// <summary>
    /// Parses the JSON emitted by <c>ffprobe -v error -print_format json
    /// -show_format -show_streams</c>.
    /// </summary>
    /// <param name="json">ffprobe JSON document.</param>
    /// <returns>Probe facts about the file.</returns>
    /// <exception cref="ExportException">Thrown when the file has no video stream.</exception>
    public static MediaProbeInfo FromProbeJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new ExportException("ffprobe returned no output.");
        }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        string? videoCodec = null;
        var width = 0;
        var height = 0;
        var duration = 0d;
        var hasVideo = false;
        var hasAudio = false;
        var audioChannels = 0;
        string? audioCodec = null;

        if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
        {
            foreach (var stream in streams.EnumerateArray())
            {
                var codecType = GetString(stream, "codec_type");
                if (string.Equals(codecType, "video", StringComparison.Ordinal) && !hasVideo && !IsAttachedPicture(stream))
                {
                    videoCodec = GetString(stream, "codec_name");
                    width = GetInt(stream, "width");
                    height = GetInt(stream, "height");
                    hasVideo = true;
                    if (duration <= 0 && TryGetDouble(stream, "duration", out var streamDuration))
                    {
                        duration = streamDuration;
                    }
                }
                else if (string.Equals(codecType, "audio", StringComparison.Ordinal) && !hasAudio)
                {
                    hasAudio = true;
                    audioChannels = GetInt(stream, "channels");
                    audioCodec = GetString(stream, "codec_name");
                    if (duration <= 0 && TryGetDouble(stream, "duration", out var streamDuration))
                    {
                        duration = streamDuration;
                    }
                }
            }
        }

        if (!hasVideo)
        {
            throw new ExportException("The source file has no video stream to export.");
        }

        if (root.TryGetProperty("format", out var format) && TryGetDouble(format, "duration", out var formatDuration))
        {
            duration = formatDuration;
        }

        return new MediaProbeInfo
        {
            VideoCodec = videoCodec,
            Width = width,
            Height = height,
            DurationSeconds = duration > 0 ? duration : 0,
            HasAudio = hasAudio,
            AudioChannels = audioChannels,
            AudioCodec = audioCodec
        };
    }

    private static bool IsAttachedPicture(JsonElement stream)
    {
        return stream.TryGetProperty("disposition", out var disposition)
            && disposition.ValueKind == JsonValueKind.Object
            && GetInt(disposition, "attached_pic") == 1;
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int GetInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed) ? parsed : 0;

    private static bool TryGetDouble(JsonElement element, string name, out double value)
    {
        value = 0;
        if (!element.TryGetProperty(name, out var property))
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out value))
        {
            return true;
        }

        if (property.ValueKind == JsonValueKind.String &&
            double.TryParse(property.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            return true;
        }

        return false;
    }
}
