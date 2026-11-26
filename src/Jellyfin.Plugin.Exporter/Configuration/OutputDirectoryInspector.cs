using System;
using System.IO;

namespace Jellyfin.Plugin.Exporter.Configuration;

/// <summary>
/// Answers "could the exporter actually write here?" for an output directory.
/// </summary>
public interface IOutputDirectoryInspector
{
    /// <summary>
    /// Checks a candidate output directory without creating it.
    /// </summary>
    /// <param name="path">The configured path.</param>
    /// <returns>Whether the path is usable and, when not, a message for the operator.</returns>
    DirectoryProbeResult Probe(string? path);
}

/// <summary>
/// Result of <see cref="IOutputDirectoryInspector.Probe"/>.
/// </summary>
/// <param name="IsValid">True when the exporter can write to the directory.</param>
/// <param name="Message">Operator-facing explanation, empty when valid.</param>
/// <param name="WillBeCreated">True when the directory does not exist yet but is creatable.</param>
public readonly record struct DirectoryProbeResult(bool IsValid, string Message, bool WillBeCreated);

/// <summary>
/// Default inspector: verifies the path is absolute, is not a file, and that
/// the nearest existing ancestor (the directory itself when it exists) accepts
/// a temporary file. The configured directory is never created by validation.
/// </summary>
public sealed class OutputDirectoryInspector : IOutputDirectoryInspector
{
    /// <inheritdoc />
    public DirectoryProbeResult Probe(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new DirectoryProbeResult(false, "An output directory is required.", false);
        }

        var trimmed = path.Trim();
        try
        {
            if (!Path.IsPathFullyQualified(trimmed))
            {
                return new DirectoryProbeResult(
                    false,
                    "The output directory must be an absolute path, for example /media/exports or D:\\Exports.",
                    false);
            }

            if (Path.GetInvalidPathChars().Length > 0 && trimmed.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            {
                return new DirectoryProbeResult(false, "The output directory contains characters that are not allowed in a path.", false);
            }

            if (File.Exists(trimmed))
            {
                return new DirectoryProbeResult(false, "The output directory points at a file, not a directory.", false);
            }

            if (Directory.Exists(trimmed))
            {
                // Reject a read-only directory up front so the export does not fail after encoding.
                var (writable, message) = CanWriteTo(trimmed);
                return writable
                    ? new DirectoryProbeResult(true, string.Empty, false)
                    : new DirectoryProbeResult(false, $"The output directory is not writable: {message}", false);
            }

            var ancestor = NearestExistingDirectory(trimmed);
            if (ancestor is null)
            {
                return new DirectoryProbeResult(false, "The output directory cannot be created: none of its parent directories exist.", false);
            }

            var (ancestorWritable, ancestorMessage) = CanWriteTo(ancestor);
            return ancestorWritable
                ? new DirectoryProbeResult(true, string.Empty, true)
                : new DirectoryProbeResult(
                    false,
                    $"The output directory cannot be created because {ancestor} is not writable: {ancestorMessage}",
                    false);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException)
        {
            return new DirectoryProbeResult(false, $"The output directory is not usable: {ex.Message}", false);
        }
    }

    /// <summary>
    /// Walks up the path until it finds a directory that exists.
    /// </summary>
    public static string? NearestExistingDirectory(string path)
    {
        var current = path;
        while (!string.IsNullOrEmpty(current))
        {
            if (Directory.Exists(current))
            {
                return current;
            }

            var parent = Path.GetDirectoryName(current);
            if (string.Equals(parent, current, StringComparison.Ordinal))
            {
                return null;
            }

            current = parent;
        }

        return null;
    }

    private static (bool Ok, string Message) CanWriteTo(string directory)
    {
        var probeFile = Path.Combine(directory, $".jellyfin-exporter-write-probe-{Guid.NewGuid():N}");
        try
        {
            using (new FileStream(probeFile, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
            }

            return (true, string.Empty);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return (false, ex.Message);
        }
        finally
        {
            try
            {
                if (File.Exists(probeFile))
                {
                    File.Delete(probeFile);
                }
            }
            catch (IOException)
            {
                // Best effort cleanup: the probe file is hidden and uniquely named.
            }
        }
    }
}
