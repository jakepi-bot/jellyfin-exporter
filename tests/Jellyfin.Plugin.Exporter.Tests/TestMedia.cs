using System.Diagnostics;
using System.Globalization;
using Jellyfin.Plugin.Exporter.Export;

namespace Jellyfin.Plugin.Exporter.Tests;

/// <summary>
/// Real FFmpeg helpers for the integration tests: generate a deterministic
/// source clip, probe an output, and check which encoders this FFmpeg build has.
/// </summary>
internal static class TestMedia
{
    public static string FfmpegPath { get; } = Environment.GetEnvironmentVariable("FFMPEG_PATH") ?? "/usr/bin/ffmpeg";

    public static string FfprobePath { get; } = Environment.GetEnvironmentVariable("FFPROBE_PATH") ?? "/usr/bin/ffprobe";

    public static bool Available => File.Exists(FfmpegPath) && File.Exists(FfprobePath);

    /// <summary>
    /// Creates a clip with a video stream and a sine audio stream.
    /// </summary>
    public static string CreateSource(
        string directory,
        string name,
        int width,
        int height,
        int audioChannels,
        double seconds = 2)
    {
        var path = Path.Combine(directory, name + ".mkv");
        var duration = seconds.ToString("0.###", CultureInfo.InvariantCulture);
        var arguments = new List<string>
        {
            "-hide_banner",
            "-y",
            "-loglevel",
            "error",
            "-f",
            "lavfi",
            "-i",
            string.Format(CultureInfo.InvariantCulture, "testsrc2=size={0}x{1}:rate=24:duration={2}", width, height, duration),
            "-f",
            "lavfi",
            "-i",
            string.Format(CultureInfo.InvariantCulture, "sine=frequency=440:sample_rate=48000:duration={0}", duration),
            "-c:v",
            "libx264",
            "-preset",
            "ultrafast",
            "-pix_fmt",
            "yuv420p",
            "-c:a",
            "aac",
            "-ac",
            audioChannels.ToString(CultureInfo.InvariantCulture),
            "-shortest",
            path
        };

        Run(FfmpegPath, arguments);
        return path;
    }

    public static string Run(string executable, IEnumerable<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start {executable}.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{executable} {string.Join(' ', arguments)} failed: {stderr}");
        }

        return stdout;
    }

    public static MediaProbeInfo Probe(string path) => MediaProbeInfo.FromProbeJson(Run(
        FfprobePath,
        new[] { "-v", "error", "-print_format", "json", "-show_format", "-show_streams", path }));

    public static bool HasEncoder(string encoder) =>
        Run(FfmpegPath, new[] { "-hide_banner", "-encoders" })
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Any(line => line.Contains(encoder, StringComparison.Ordinal));

    /// <summary>
    /// Temporary directory that cleans itself up.
    /// </summary>
    public sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "jfx-tests-" + Guid.NewGuid().ToString("N")[..12]);
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string Combine(params string[] parts) => System.IO.Path.Combine(new[] { Path }.Concat(parts).ToArray());

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
