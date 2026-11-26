using System;
using System.Globalization;

namespace Jellyfin.Plugin.Exporter.Export;

/// <summary>
/// Turns the key=value lines FFmpeg writes for <c>-progress pipe:1</c> into a
/// percentage of the source duration. FFmpeg prints both
/// <c>out_time_us</c> and <c>out_time_ms</c> in microseconds.
/// </summary>
public static class FfmpegProgressParser
{
    /// <summary>Highest percentage reported from the stream; 100 is set when the process exits cleanly.</summary>
    public const double MaxStreamedPercent = 99.9;

    /// <summary>
    /// Parses one progress line.
    /// </summary>
    /// <param name="line">Line from FFmpeg's progress stream.</param>
    /// <param name="durationSeconds">Source duration in seconds; 0 when unknown.</param>
    /// <returns>Percentage in 0-99.9, or null when the line carries no position.</returns>
    public static double? ParsePercent(string? line, double durationSeconds)
    {
        if (string.IsNullOrWhiteSpace(line) || durationSeconds <= 0)
        {
            return null;
        }

        if (!TryParseSeconds(line.Trim(), out var seconds))
        {
            return null;
        }

        var percent = seconds / durationSeconds * 100d;
        if (double.IsNaN(percent) || double.IsInfinity(percent))
        {
            return null;
        }

        return Math.Clamp(percent, 0d, MaxStreamedPercent);
    }

    /// <summary>
    /// Parses the position out of a progress line.
    /// </summary>
    /// <param name="line">Line from FFmpeg's progress stream.</param>
    /// <param name="seconds">Position in seconds.</param>
    /// <returns>True when the line carried a position.</returns>
    public static bool TryParseSeconds(string? line, out double seconds)
    {
        seconds = 0;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var trimmed = line.Trim();

        if (TryGetValue(trimmed, "out_time_us=", out var micros) || TryGetValue(trimmed, "out_time_ms=", out micros))
        {
            if (long.TryParse(micros, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedMicros) && parsedMicros >= 0)
            {
                seconds = parsedMicros / 1_000_000d;
                return true;
            }
        }

        if (TryGetValue(trimmed, "out_time=", out var time))
        {
            return TimeSpan.TryParse(time, CultureInfo.InvariantCulture, out var span) && (seconds = span.TotalSeconds) >= 0;
        }

        return false;
    }

    private static bool TryGetValue(string line, string prefix, out string value)
    {
        if (line.StartsWith(prefix, StringComparison.Ordinal))
        {
            value = line[prefix.Length..];
            return true;
        }

        value = string.Empty;
        return false;
    }
}
