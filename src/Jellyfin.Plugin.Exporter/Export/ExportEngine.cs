using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Exporter.Models;
using Jellyfin.Plugin.Exporter.Services;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Exporter.Export;

/// <summary>
/// Terminal outcome of one engine run, mapped onto the job status by the queue
/// worker.
/// </summary>
/// <param name="Outcome">Completed, Failed or Cancelled.</param>
/// <param name="OutputPath">Final file path for a completed export.</param>
/// <param name="ErrorMessage">Operator-facing error for a failed export.</param>
public sealed record ExportRunResult(ExportJobStatus Outcome, string? OutputPath, string? ErrorMessage)
{
    /// <summary>Creates a completed result.</summary>
    /// <param name="outputPath">Final file path.</param>
    /// <returns>Result.</returns>
    public static ExportRunResult Completed(string outputPath) => new(ExportJobStatus.Completed, outputPath, null);

    /// <summary>Creates a failed result.</summary>
    /// <param name="message">Operator-facing error.</param>
    /// <returns>Result.</returns>
    public static ExportRunResult Failed(string message) => new(ExportJobStatus.Failed, null, message);

    /// <summary>Creates a cancelled result.</summary>
    /// <param name="message">Optional reason.</param>
    /// <returns>Result.</returns>
    public static ExportRunResult Cancelled(string? message = null) => new(ExportJobStatus.Cancelled, null, message);
}

/// <summary>
/// Runs one export job end to end: probe the source, build the FFmpeg command,
/// encode into a hidden partial file in the destination folder, then rename it
/// into place atomically.
/// </summary>
public interface IExportEngine
{
    /// <summary>
    /// Exports one job.
    /// </summary>
    /// <param name="job">Job (its snapshot decides format, resolution, audio and directory).</param>
    /// <param name="item">Resolved source file and layout metadata.</param>
    /// <param name="progress">Receives percentages 0-99.9 while FFmpeg runs.</param>
    /// <param name="cancellationToken">Cancels the encode and removes partial output.</param>
    /// <returns>The terminal outcome. Throws only for unexpected failures.</returns>
    Task<ExportRunResult> RunAsync(
        ExportJob job,
        ExportItemInfo item,
        Action<double>? progress,
        CancellationToken cancellationToken);
}

/// <summary>
/// Default <see cref="IExportEngine"/>: Jellyfin's FFmpeg, one process per job.
/// </summary>
public sealed class ExportEngine : IExportEngine
{
    private const int MaxErrorCharacters = 2000;
    private const int ErrorBufferCharacters = 64 * 1024;

    private readonly IFfmpegLocator _locator;
    private readonly PathReservationRegistry _reservations;
    private readonly ILogger<ExportEngine> _logger;

    public ExportEngine(
        IFfmpegLocator locator,
        PathReservationRegistry reservations,
        ILogger<ExportEngine> logger)
    {
        _locator = locator;
        _reservations = reservations;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<ExportRunResult> RunAsync(
        ExportJob job,
        ExportItemInfo item,
        Action<double>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(item);
        item.Validate();

        if (string.IsNullOrWhiteSpace(job.OutputDirectory))
        {
            throw new ExportException("The job has no output directory.");
        }

        var location = _locator.Locate();
        cancellationToken.ThrowIfCancellationRequested();

        var probe = await ProbeAsync(location.ProbePath, item.SourcePath, cancellationToken).ConfigureAwait(false);
        var desiredPath = OutputLayout.GetFilePath(job.OutputDirectory, item, FfmpegCommandBuilder.ContainerExtension);
        var finalPath = _reservations.ReserveUnique(desiredPath);

        try
        {
            var directory = Path.GetDirectoryName(finalPath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                throw new ExportException($"The output path has no directory: {finalPath}");
            }

            Directory.CreateDirectory(directory);

            var tempPath = Path.Combine(
                directory,
                string.Format(
                    CultureInfo.InvariantCulture,
                    ".{0}.{1}.partial",
                    Path.GetFileName(finalPath),
                    job.Id.ToString("N", CultureInfo.InvariantCulture)[..8]));

            DeleteFileIfPresent(tempPath);

            var command = FfmpegCommandBuilder.Build(
                job,
                probe,
                item.SourcePath,
                tempPath,
                location.HwAccel,
                location.SvtAv1Available);

            _logger.LogInformation(
                "Export {JobId} of '{Item}': {Description} -> {OutputPath}",
                job.Id,
                item.Title,
                command.Describe(),
                finalPath);
            _logger.LogDebug(
                "Export {JobId} FFmpeg command: {Encoder} {Arguments}",
                job.Id,
                location.EncoderPath,
                string.Join(' ', command.Arguments));

            return await EncodeAsync(
                location.EncoderPath,
                command,
                tempPath,
                finalPath,
                probe.DurationSeconds,
                progress,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _reservations.Release(finalPath);
        }
    }

    private async Task<ExportRunResult> EncodeAsync(
        string encoderPath,
        FfmpegCommand command,
        string tempPath,
        string finalPath,
        double durationSeconds,
        Action<double>? progress,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = encoderPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (var argument in command.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            DeleteFileIfPresent(tempPath);
            return ExportRunResult.Failed("Could not start FFmpeg.");
        }

        var stderr = new StringBuilder();
        var stderrTask = DrainAsync(process.StandardError, stderr);
        using var registration = cancellationToken.Register(() => Kill(process));

        try
        {
            while (true)
            {
                var line = await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                var percent = FfmpegProgressParser.ParsePercent(line, durationSeconds);
                if (percent is double value)
                {
                    progress?.Invoke(value);
                }
            }
        }
        catch (OperationCanceledException)
        {
            Kill(process);
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Reading FFmpeg progress failed; waiting for the process to exit.");
        }

        try
        {
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogDebug(ex, "FFmpeg process was already gone.");
        }

        var errorText = await stderrTask.ConfigureAwait(false);

        if (cancellationToken.IsCancellationRequested)
        {
            DeleteFileIfPresent(tempPath);
            return ExportRunResult.Cancelled("The export was cancelled.");
        }

        if (process.ExitCode != 0)
        {
            DeleteFileIfPresent(tempPath);
            return ExportRunResult.Failed(DescribeFailure(process.ExitCode, errorText));
        }

        if (!File.Exists(tempPath) || new FileInfo(tempPath).Length == 0)
        {
            DeleteFileIfPresent(tempPath);
            return ExportRunResult.Failed("FFmpeg exited successfully but produced no output file.");
        }

        try
        {
            // Same directory, so this is an atomic rename: a partial export is
            // never visible under the final name.
            File.Move(tempPath, finalPath, overwrite: false);
        }
        catch (IOException ex)
        {
            DeleteFileIfPresent(tempPath);
            return ExportRunResult.Failed($"Could not move the finished export into place: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            DeleteFileIfPresent(tempPath);
            return ExportRunResult.Failed($"Could not write the export to '{finalPath}': {ex.Message}");
        }

        progress?.Invoke(100);
        return ExportRunResult.Completed(finalPath);
    }

    private async Task<MediaProbeInfo> ProbeAsync(string probePath, string sourcePath, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = probePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (var argument in new[] { "-v", "error", "-print_format", "json", "-show_format", "-show_streams", sourcePath })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new ExportException($"Could not start ffprobe ({probePath}).");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var json = await stdoutTask.ConfigureAwait(false);
        var error = await stderrTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            throw new ExportException(
                $"ffprobe could not read '{sourcePath}': {Shorten(error)}");
        }

        return MediaProbeInfo.FromProbeJson(json);
    }

    private static async Task<string> DrainAsync(StreamReader reader, StringBuilder sink)
    {
        var buffer = new char[4096];
        while (true)
        {
            var read = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }

            sink.Append(buffer, 0, read);
            if (sink.Length > ErrorBufferCharacters)
            {
                sink.Remove(0, sink.Length - ErrorBufferCharacters);
            }
        }

        return sink.ToString();
    }

    private static string DescribeFailure(int exitCode, string errorText)
    {
        var detail = Shorten(errorText);
        return detail.Length == 0
            ? string.Format(CultureInfo.InvariantCulture, "FFmpeg exited with code {0}.", exitCode)
            : string.Format(CultureInfo.InvariantCulture, "FFmpeg exited with code {0}: {1}", exitCode, detail);
    }

    private static string Shorten(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var collapsed = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length <= MaxErrorCharacters
            ? collapsed
            : collapsed[^MaxErrorCharacters..];
    }

    private void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Killing the FFmpeg process tree failed; it may have exited already.");
        }
    }

    private void DeleteFileIfPresent(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not delete partial output '{Path}'.", path);
        }
    }
}
