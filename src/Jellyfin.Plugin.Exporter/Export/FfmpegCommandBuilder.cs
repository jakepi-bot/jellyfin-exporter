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
/// Decided hardware acceleration for one export format: the family and, for
/// VA-API/QSV, the render device the exact encoder was verified on.
/// </summary>
/// <param name="Kind">Acceleration family, or <see cref="HwAccelKind.None"/> for software.</param>
/// <param name="DevicePath">Render device for this encoder, when hardware is used.</param>
public sealed record FormatAcceleration(HwAccelKind Kind, string? DevicePath = null)
{
    /// <summary>Gets the software decision for a format.</summary>
    public static FormatAcceleration Software { get; } = new(HwAccelKind.None);

    /// <summary>Gets a value indicating whether a hardware encoder is used for this format.</summary>
    public bool IsHardware => Kind != HwAccelKind.None;
}

/// <summary>
/// Decided hardware acceleration for this server: the family, the device the
/// family was configured with, and — when the exact encoders were verified
/// per codec — which device (or software) each export format gets.
/// </summary>
/// <param name="Kind">Acceleration family.</param>
/// <param name="DevicePath">Configured render device for the family (VA-API or QSV).</param>
public sealed record HwAccelPlan(HwAccelKind Kind, string? DevicePath = null)
{
    /// <summary>Gets the software-encoding plan.</summary>
    public static HwAccelPlan Software { get; } = new(HwAccelKind.None);

    /// <summary>
    /// Gets the per-format decisions from the exact-encoder verification.
    /// Null when detection only verified the family (legacy behaviour: the
    /// family plan applies to every format).
    /// </summary>
    public IReadOnlyDictionary<ExportFormat, FormatAcceleration>? Formats { get; init; }

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

    /// <summary>
    /// The decision for one export format: the exact device its encoder was
    /// verified on, or software when no device could encode it. This is where
    /// a per-job AV1 export stops using <c>av1_vaapi</c> on a device that
    /// cannot encode AV1.
    /// </summary>
    /// <param name="format">Profile format.</param>
    /// <returns>The format's acceleration decision.</returns>
    public FormatAcceleration ForFormat(ExportFormat format) =>
        Formats is not null && Formats.TryGetValue(format, out var resolved)
            ? resolved
            : new FormatAcceleration(Kind, DevicePath);
}

/// <summary>
/// The encoder a job will actually run: name, whether it is hardware, and the
/// device its family needs. Produced by
/// <see cref="FfmpegCommandBuilder.ResolveVideoEncoder"/> so the command and
/// the diagnostics can never disagree.
/// </summary>
/// <param name="Encoder">Exact FFmpeg encoder name, e.g. <c>av1_vaapi</c> or <c>libsvtav1</c>.</param>
/// <param name="Hardware">Whether the encoder is a hardware one.</param>
/// <param name="Kind">Acceleration family of the encoder (software when <see cref="HwAccelKind.None"/>).</param>
/// <param name="DevicePath">Device to pass to FFmpeg (<c>-vaapi_device</c>/<c>-qsv_device</c>), when the encoder needs one.</param>
public readonly record struct VideoEncoderChoice(string Encoder, bool Hardware, HwAccelKind Kind, string? DevicePath);

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
    /// <param name="jellyfin">
    /// Jellyfin's transcoding settings, honoured when the job's profile has
    /// <c>UseJellyfinTranscodingSettings</c> on; null keeps the plugin defaults.
    /// </param>
    /// <returns>The complete invocation.</returns>
    public static FfmpegCommand Build(
        ExportJob job,
        MediaProbeInfo probe,
        string inputPath,
        string outputPath,
        HwAccelPlan? hwAccel = null,
        bool svtAv1Available = true,
        JellyfinTranscodingSettings? jellyfin = null)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        // Jellyfin's settings only apply to profiles that asked for them; a
        // profile with the toggle off keeps the plugin's own encoder settings.
        var settings = job.UseJellyfinTranscodingSettings ? jellyfin : null;

        var accel = hwAccel ?? HwAccelPlan.Software;
        if (settings is { EnableHardwareEncoding: false })
        {
            // Jellyfin's transcoding settings say no hardware: every format
            // encodes in software, whatever the detection plan found.
            accel = HwAccelPlan.Software;
        }

        var (targetWidth, targetHeight) = GetTargetDimensions(job.Resolution);
        var (width, height) = ScaleToFit(probe.Width, probe.Height, targetWidth, targetHeight);
        var video = ResolveVideoEncoder(job.Format, accel, svtAv1Available);
        var videoOptions = video.Hardware
            ? HardwareOptions(video.Kind, job.Format, settings)
            : SoftwareOptions(job.Format, svtAv1Available, settings);

        var arguments = new List<string>
        {
            "-hide_banner",
            "-nostdin",
            "-y",
            "-loglevel",
            "error"
        };

        if (video.Hardware && video.Kind == HwAccelKind.Vaapi)
        {
            arguments.Add("-vaapi_device");
            arguments.Add(string.IsNullOrWhiteSpace(video.DevicePath) ? DefaultVaapiDevice : video.DevicePath!);
        }

        if (video.Hardware && video.Kind == HwAccelKind.Qsv && !string.IsNullOrWhiteSpace(video.DevicePath))
        {
            // The QSV device comes from EncodingOptions.QsvDevice; without it
            // FFmpeg picks its default adapter, which is not necessarily the
            // one the operator configured.
            arguments.Add("-qsv_device");
            arguments.Add(video.DevicePath!);
        }

        arguments.Add("-i");
        arguments.Add(inputPath);
        arguments.Add("-map");
        arguments.Add("0:v:0");
        arguments.Add("-map");
        arguments.Add("0:a?");
        arguments.Add("-vf");
        arguments.Add(BuildScaleFilter(width, height, video));
        arguments.Add("-c:v");
        arguments.Add(video.Encoder);
        arguments.AddRange(videoOptions);
        if (!video.Hardware && settings is { EncodingThreadCount: > 0 })
        {
            // FFmpeg's own default when unset; only software encoding is
            // thread-capped (hardware encoders manage their own queues).
            arguments.Add("-threads");
            arguments.Add(settings.EncodingThreadCount.ToString(CultureInfo.InvariantCulture));
        }

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
    /// Resolves the exact encoder a job will run: the hardware encoder that
    /// was verified for this format on its device, or the software encoder.
    /// The per-format decision lives here, at command-build time, so a job in
    /// an AV1 profile can never be handed <c>av1_vaapi</c> on a device that
    /// cannot encode AV1.
    /// </summary>
    /// <param name="format">Profile format.</param>
    /// <param name="accel">Acceleration plan for this server.</param>
    /// <param name="svtAv1Available">Whether FFmpeg has libsvtav1.</param>
    /// <returns>The encoder choice, including the device FFmpeg needs.</returns>
    public static VideoEncoderChoice ResolveVideoEncoder(
        ExportFormat format,
        HwAccelPlan accel,
        bool svtAv1Available)
    {
        ArgumentNullException.ThrowIfNull(accel);

        var codec = accel.ForFormat(format);
        var suffix = codec.Kind switch
        {
            HwAccelKind.Cuda => "nvenc",
            HwAccelKind.Vaapi => "vaapi",
            HwAccelKind.Qsv => "qsv",
            HwAccelKind.VideoToolbox => "videotoolbox",
            _ => null
        };

        if (suffix is not null && !(codec.Kind == HwAccelKind.VideoToolbox && format == ExportFormat.AV1))
        {
            var prefix = format switch
            {
                ExportFormat.H264 => "h264",
                ExportFormat.H265 => "hevc",
                _ => "av1"
            };

            string? device = codec.Kind switch
            {
                HwAccelKind.Vaapi => string.IsNullOrWhiteSpace(codec.DevicePath) ? DefaultVaapiDevice : codec.DevicePath,
                HwAccelKind.Qsv => string.IsNullOrWhiteSpace(codec.DevicePath) ? null : codec.DevicePath,
                _ => null
            };

            return new VideoEncoderChoice(prefix + "_" + suffix, true, codec.Kind, device);
        }

        return new VideoEncoderChoice(SoftwareEncoderName(format, svtAv1Available), false, HwAccelKind.None, null);
    }

    /// <summary>
    /// The software encoder for a format.
    /// </summary>
    /// <param name="format">Profile format.</param>
    /// <param name="svtAv1Available">Whether FFmpeg has libsvtav1.</param>
    /// <returns><c>libx264</c>, <c>libx265</c>, <c>libsvtav1</c> or <c>libaom-av1</c>.</returns>
    public static string SoftwareEncoderName(ExportFormat format, bool svtAv1Available) => format switch
    {
        ExportFormat.H265 => "libx265",
        ExportFormat.AV1 => svtAv1Available ? "libsvtav1" : "libaom-av1",
        _ => "libx264"
    };

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

    private static string BuildScaleFilter(int width, int height, VideoEncoderChoice video)
    {
        var scale = string.Format(CultureInfo.InvariantCulture, "scale={0}:{1}", width, height);
        if (video.Hardware && video.Kind == HwAccelKind.Vaapi)
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

    private static IReadOnlyList<string> HardwareOptions(
        HwAccelKind kind,
        ExportFormat format,
        JellyfinTranscodingSettings? settings)
    {
        var preset = settings is { HonoursPreset: true }
            ? JellyfinPresetMap.HardwarePreset(kind, settings.EncoderPreset)
            : null;

        switch (kind)
        {
            case HwAccelKind.Cuda:
                return new[] { "-preset", preset ?? "p5", "-rc", "vbr", "-cq", "23", "-b:v", "0" };
            case HwAccelKind.Qsv:
                return new[] { "-preset", preset ?? "medium", "-global_quality", "23" };
            case HwAccelKind.Vaapi:
                // VA-API encoders have no preset flag; the quality knob is -qp.
                return new[] { "-qp", "23" };
            case HwAccelKind.VideoToolbox:
                return new[] { "-q:v", "55" };
            default:
                return Array.Empty<string>();
        }
    }

    private static IReadOnlyList<string> SoftwareOptions(
        ExportFormat format,
        bool svtAv1Available,
        JellyfinTranscodingSettings? settings)
    {
        var preset = settings is { HonoursPreset: true } ? settings.EncoderPreset.Trim() : null;

        switch (format)
        {
            case ExportFormat.H265:
                return new[]
                {
                    "-preset", JellyfinPresetMap.X26xPreset(preset) ?? "medium",
                    "-crf", (settings?.H265Crf ?? 24).ToString(CultureInfo.InvariantCulture),
                    "-pix_fmt", "yuv420p"
                };
            case ExportFormat.AV1:
                // Jellyfin has no AV1 CRF knob, so AV1 keeps the plugin's
                // documented SVT-AV1 settings; only the preset is mapped.
                if (svtAv1Available)
                {
                    return new[]
                    {
                        "-preset", JellyfinPresetMap.SvtPreset(preset) ?? "8",
                        "-crf", "32",
                        "-pix_fmt", "yuv420p"
                    };
                }

                return new[]
                {
                    "-crf", "32",
                    "-b:v", "0",
                    "-cpu-used", JellyfinPresetMap.AomCpuUsed(preset) ?? "8",
                    "-row-mt", "1",
                    "-pix_fmt", "yuv420p"
                };
            default:
                return new[]
                {
                    "-preset", JellyfinPresetMap.X26xPreset(preset) ?? "medium",
                    "-crf", (settings?.H264Crf ?? 20).ToString(CultureInfo.InvariantCulture),
                    "-profile:v", "high",
                    "-pix_fmt", "yuv420p"
                };
        }
    }

    private static int MakeEven(int value) => value % 2 == 0 ? value : value - 1;
}
