using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.Exporter.Models;

namespace Jellyfin.Plugin.Exporter.Export;

/// <summary>
/// Hardware acceleration family the exporter can encode with. Detection fills
/// this in; the command builder only reacts to it.
/// </summary>
public enum HwAccelKind
{
    /// <summary>Software encoding (libx264, libx265, libsvtav1/libaom-av1).</summary>
    None = 0,

    /// <summary>NVIDIA NVENC (<c>-hwaccel cuda</c>, <c>*_nvenc</c>).</summary>
    Cuda = 1,

    /// <summary>VA-API (<c>*_vaapi</c>).</summary>
    Vaapi = 2,

    /// <summary>Intel Quick Sync (<c>*_qsv</c>).</summary>
    Qsv = 3,

    /// <summary>Apple VideoToolbox (<c>*_videotoolbox</c>).</summary>
    VideoToolbox = 4
}

/// <summary>
/// Decided hardware acceleration for this server: the family and, for VA-API,
/// the render device to hand to FFmpeg.
/// </summary>
/// <param name="Kind">Acceleration family.</param>
/// <param name="DevicePath">VA-API render device.</param>
public sealed record HwAccelPlan(HwAccelKind Kind, string? DevicePath = null)
{
    /// <summary>Gets the software-encoding plan.</summary>
    public static HwAccelPlan Software { get; } = new(HwAccelKind.None);

    /// <summary>Gets a value indicating whether a hardware encoder is used.</summary>
    public bool IsHardware => Kind != HwAccelKind.None;

    /// <summary>Gets the value FFmpeg expects for <c>-hwaccel</c>.</summary>
    public string FfmpegName => Kind switch
    {
        HwAccelKind.Cuda => "cuda",
        HwAccelKind.Vaapi => "vaapi",
        HwAccelKind.Qsv => "qsv",
        HwAccelKind.VideoToolbox => "videotoolbox",
        _ => "none"
    };
}

/// <summary>
/// One fully built FFmpeg invocation: the argv list, the muxer and the facts
/// the engine reports back to the operator.
/// </summary>
public sealed record FfmpegCommand
{
    /// <summary>Gets the complete argument list, including the output path.</summary>
    public IReadOnlyList<string> Arguments { get; init; } = Array.Empty<string>();

    /// <summary>Gets the FFmpeg muxer name (<c>matroska</c>).</summary>
    public string Muxer { get; init; } = FfmpegCommandBuilder.ContainerMuxer;

    /// <summary>Gets the output file extension without a dot (<c>mkv</c>).</summary>
    public string OutputExtension { get; init; } = FfmpegCommandBuilder.ContainerExtension;

    /// <summary>Gets the video encoder name, e.g. <c>libx264</c> or <c>hevc_nvenc</c>.</summary>
    public string VideoEncoder { get; init; } = string.Empty;

    /// <summary>Gets the audio codec: <c>copy</c> or <c>aac</c>.</summary>
    public string AudioCodec { get; init; } = string.Empty;

    /// <summary>Gets a value indicating whether the video encoder is a hardware one.</summary>
    public bool HardwareVideoEncoder { get; init; }

    /// <summary>Gets the output width in pixels.</summary>
    public int Width { get; init; }

    /// <summary>Gets the output height in pixels.</summary>
    public int Height { get; init; }

    /// <summary>Gets a one-line description for logs.</summary>
    public string Describe() =>
        string.Format(
            CultureInfo.InvariantCulture,
            "{0} {1}x{2} audio={3} ({4})",
            VideoEncoder,
            Width,
            Height,
            AudioCodec,
            HardwareVideoEncoder ? "hardware" : "software");
}

/// <summary>
/// Builds the FFmpeg command line for one job. Pure: everything it needs comes
/// in through the parameters, so every codec/resolution/audio/hwaccel
/// combination is unit-testable without a Jellyfin server or a GPU.
/// </summary>
public static class FfmpegCommandBuilder
{
    /// <summary>Container used for every export.</summary>
    /// <remarks>
    /// Matroska holds every video codec the profiles offer and every audio
    /// codec an "Original" profile may copy, so a job cannot fail because the
    /// container rejects the source audio (DTS-HD, TrueHD, FLAC, ...).
    /// </remarks>
    public const string ContainerExtension = "mkv";

    /// <summary>FFmpeg muxer name for <see cref="ContainerExtension"/>.</summary>
    public const string ContainerMuxer = "matroska";

    /// <summary>Default VA-API render device.</summary>
    public const string DefaultVaapiDevice = "/dev/dri/renderD128";

    /// <summary>
    /// Builds the command for one job.
    /// </summary>
    /// <param name="job">Job whose snapshot carries format, resolution and audio mode.</param>
    /// <param name="probe">Source facts: dimensions, duration, audio.</param>
    /// <param name="inputPath">Absolute source file path.</param>
    /// <param name="outputPath">Temporary output path FFmpeg writes.</param>
    /// <param name="hwAccel">Acceleration plan; software when null.</param>
    /// <param name="svtAv1Available">Whether FFmpeg has libsvtav1 (faster AV1).</param>
    /// <returns>The complete invocation.</returns>
    public static FfmpegCommand Build(
        ExportJob job,
        MediaProbeInfo probe,
        string inputPath,
        string outputPath,
        HwAccelPlan? hwAccel = null,
        bool svtAv1Available = true)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var accel = hwAccel ?? HwAccelPlan.Software;
        var (targetWidth, targetHeight) = GetTargetDimensions(job.Resolution);
        var (width, height) = ScaleToFit(probe.Width, probe.Height, targetWidth, targetHeight);
        var video = SelectVideoEncoder(job.Format, accel, svtAv1Available, targetWidth);

        var arguments = new List<string>
        {
            "-hide_banner",
            "-nostdin",
            "-y",
            "-loglevel",
            "error"
        };

        if (video.Hardware && accel.Kind == HwAccelKind.Vaapi)
        {
            arguments.Add("-vaapi_device");
            arguments.Add(string.IsNullOrWhiteSpace(accel.DevicePath) ? DefaultVaapiDevice : accel.DevicePath!);
        }

        arguments.Add("-i");
        arguments.Add(inputPath);
        arguments.Add("-map");
        arguments.Add("0:v:0");
        arguments.Add("-map");
        arguments.Add("0:a?");
        arguments.Add("-vf");
        arguments.Add(BuildScaleFilter(width, height, accel, video.Hardware));
        arguments.Add("-c:v");
        arguments.Add(video.Encoder);
        arguments.AddRange(video.Options);
        var audioCodec = AddAudioArguments(job.AudioChannels, arguments);
        arguments.Add("-max_muxing_queue_size");
        arguments.Add("1024");
        arguments.Add("-progress");
        arguments.Add("pipe:1");
        arguments.Add("-nostats");
        arguments.Add("-f");
        arguments.Add(ContainerMuxer);
        arguments.Add(outputPath);

        return new FfmpegCommand
        {
            Arguments = arguments,
            Muxer = ContainerMuxer,
            OutputExtension = ContainerExtension,
            VideoEncoder = video.Encoder,
            AudioCodec = audioCodec,
            HardwareVideoEncoder = video.Hardware,
            Width = width,
            Height = height
        };
    }

    /// <summary>
    /// Target dimensions for a profile resolution.
    /// </summary>
    /// <param name="resolution">Profile resolution.</param>
    /// <returns>Width and height of the target box.</returns>
    public static (int Width, int Height) GetTargetDimensions(ExportResolution resolution) => resolution switch
    {
        ExportResolution.Resolution4K => (3840, 2160),
        ExportResolution.Resolution1080p => (1920, 1080),
        ExportResolution.Resolution720p => (1280, 720),
        ExportResolution.Resolution480p => (854, 480),
        _ => throw new ArgumentOutOfRangeException(nameof(resolution), resolution, "Unknown export resolution.")
    };

    /// <summary>
    /// Fits the source inside the target box without distortion and without
    /// upscaling, rounding to even dimensions (required by every codec here).
    /// </summary>
    /// <param name="sourceWidth">Source width in pixels.</param>
    /// <param name="sourceHeight">Source height in pixels.</param>
    /// <param name="targetWidth">Target box width.</param>
    /// <param name="targetHeight">Target box height.</param>
    /// <returns>Output width and height.</returns>
    public static (int Width, int Height) ScaleToFit(int sourceWidth, int sourceHeight, int targetWidth, int targetHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0)
        {
            return (targetWidth, targetHeight);
        }

        var scale = Math.Min(1d, Math.Min((double)targetWidth / sourceWidth, (double)targetHeight / sourceHeight));
        var width = MakeEven((int)Math.Round(sourceWidth * scale, MidpointRounding.AwayFromZero));
        var height = MakeEven((int)Math.Round(sourceHeight * scale, MidpointRounding.AwayFromZero));
        return (Math.Clamp(width, 2, targetWidth), Math.Clamp(height, 2, targetHeight));
    }

    /// <summary>
    /// Documented target bitrate per codec and resolution. Used for encoders
    /// that are bitrate-driven (VideoToolbox) and to explain the CRF choices.
    /// </summary>
    /// <param name="format">Profile format.</param>
    /// <param name="resolution">Profile resolution.</param>
    /// <returns>Bitrate such as <c>6M</c>.</returns>
    public static string GetTargetBitrate(ExportFormat format, ExportResolution resolution) => (format, resolution) switch
    {
        (ExportFormat.H264, ExportResolution.Resolution4K) => "15M",
        (ExportFormat.H264, ExportResolution.Resolution1080p) => "6M",
        (ExportFormat.H264, ExportResolution.Resolution720p) => "3M",
        (ExportFormat.H264, ExportResolution.Resolution480p) => "1500k",
        (ExportFormat.H265, ExportResolution.Resolution4K) => "10M",
        (ExportFormat.H265, ExportResolution.Resolution1080p) => "4M",
        (ExportFormat.H265, ExportResolution.Resolution720p) => "2M",
        (ExportFormat.H265, ExportResolution.Resolution480p) => "1M",
        (ExportFormat.AV1, ExportResolution.Resolution4K) => "8M",
        (ExportFormat.AV1, ExportResolution.Resolution1080p) => "3M",
        (ExportFormat.AV1, ExportResolution.Resolution720p) => "1500k",
        _ => "800k"
    };

    private static string BuildScaleFilter(int width, int height, HwAccelPlan accel, bool hardware)
    {
        var scale = string.Format(CultureInfo.InvariantCulture, "scale={0}:{1}", width, height);
        if (hardware && accel.Kind == HwAccelKind.Vaapi)
        {
            return string.Format(CultureInfo.InvariantCulture, "format=nv12,hwupload,scale_vaapi=w={0}:h={1}", width, height);
        }

        return scale;
    }

    private static string AddAudioArguments(ExportAudioChannels channels, List<string> arguments)
    {
        switch (channels)
        {
            case ExportAudioChannels.Surround51:
                arguments.AddRange(new[] { "-c:a", "aac", "-ac", "6", "-b:a", "384k" });
                return "aac";
            case ExportAudioChannels.Stereo:
                arguments.AddRange(new[] { "-c:a", "aac", "-ac", "2", "-b:a", "192k" });
                return "aac";
            default:
                arguments.AddRange(new[] { "-c:a", "copy" });
                return "copy";
        }
    }

    private static (string Encoder, IReadOnlyList<string> Options, bool Hardware) SelectVideoEncoder(
        ExportFormat format,
        HwAccelPlan accel,
        bool svtAv1Available,
        int targetWidth)
    {
        var suffix = accel.Kind switch
        {
            HwAccelKind.Cuda => "nvenc",
            HwAccelKind.Vaapi => "vaapi",
            HwAccelKind.Qsv => "qsv",
            HwAccelKind.VideoToolbox => "videotoolbox",
            _ => null
        };

        if (suffix is not null && !(accel.Kind == HwAccelKind.VideoToolbox && format == ExportFormat.AV1))
        {
            var prefix = format switch
            {
                ExportFormat.H264 => "h264",
                ExportFormat.H265 => "hevc",
                _ => "av1"
            };

            return (prefix + "_" + suffix, HardwareOptions(accel.Kind, format), true);
        }

        return format switch
        {
            ExportFormat.H265 => ("libx265", new[] { "-preset", "medium", "-crf", "24", "-pix_fmt", "yuv420p" }, false),
            ExportFormat.AV1 => svtAv1Available
                ? ("libsvtav1", new[] { "-preset", "8", "-crf", "32", "-pix_fmt", "yuv420p" }, false)
                : ("libaom-av1", new[] { "-crf", "32", "-b:v", "0", "-cpu-used", "8", "-row-mt", "1", "-pix_fmt", "yuv420p" }, false),
            _ => ("libx264", new[] { "-preset", "medium", "-crf", "20", "-profile:v", "high", "-pix_fmt", "yuv420p" }, false)
        };
    }

    private static IReadOnlyList<string> HardwareOptions(HwAccelKind kind, ExportFormat format) => kind switch
    {
        HwAccelKind.Cuda => new[] { "-preset", "p5", "-rc", "vbr", "-cq", "23", "-b:v", "0" },
        HwAccelKind.Qsv => new[] { "-preset", "medium", "-global_quality", "23" },
        HwAccelKind.Vaapi => new[] { "-qp", "23" },
        HwAccelKind.VideoToolbox => new[] { "-q:v", "55" },
        _ => Array.Empty<string>()
    };

    private static int MakeEven(int value) => value % 2 == 0 ? value : value - 1;
}
