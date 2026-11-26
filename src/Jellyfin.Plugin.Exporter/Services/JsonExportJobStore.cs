using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.Exporter.Models;

namespace Jellyfin.Plugin.Exporter.Services;

/// <summary>
/// Persists the job history as JSON in the plugin's data folder.
/// </summary>
/// <remarks>
/// The directory is resolved through a delegate instead of being captured at
/// construction time: the service container is built before the plugin's data
/// folder is guaranteed to exist. Writes go to a temporary file in the same
/// directory and are moved over the real file, so a crash mid-write cannot
/// leave a truncated history behind. A corrupt file is preserved as
/// <c>jobs.corrupt-*.json</c> rather than silently deleted.
/// </remarks>
public sealed class JsonExportJobStore : IExportJobStore
{
    /// <summary>File name inside the plugin data folder.</summary>
    public const string FileName = "jobs.json";

    /// <summary>Schema version written into the file.</summary>
    public const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(), new FlexibleGuidJsonConverter() }
    };

    private readonly Func<string?> _directoryProvider;
    private readonly object _ioGate = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="JsonExportJobStore"/> class.
    /// </summary>
    /// <param name="directory">Plugin data folder, or null to disable persistence.</param>
    public JsonExportJobStore(string? directory)
        : this(() => directory)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="JsonExportJobStore"/> class.
    /// </summary>
    /// <param name="directoryProvider">Resolves the plugin data folder on every access.</param>
    public JsonExportJobStore(Func<string?> directoryProvider)
    {
        _directoryProvider = directoryProvider ?? (() => null);
    }

    /// <inheritdoc />
    public string? Location
    {
        get
        {
            var directory = ResolveDirectory();
            return directory is null ? null : Path.Combine(directory, FileName);
        }
    }

    /// <inheritdoc />
    public string? LastError { get; private set; }

    /// <inheritdoc />
    public IReadOnlyList<ExportJob> Load()
    {
        var path = Location;
        if (path is null)
        {
            return new List<ExportJob>();
        }

        lock (_ioGate)
        {
            if (!File.Exists(path))
            {
                LastError = null;
                return new List<ExportJob>();
            }

            try
            {
                var json = File.ReadAllText(path);
                var file = JsonSerializer.Deserialize<ExportJobFile>(json, SerializerOptions);
                var jobs = file?.Jobs?
                    .Where(job => job is not null && job.Id != Guid.Empty)
                    .ToList() ?? new List<ExportJob>();
                LastError = null;
                return jobs;
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                LastError = $"Could not read {FileName}: {ex.Message}";
                QuarantineLocked(path);
                return new List<ExportJob>();
            }
        }
    }

    /// <inheritdoc />
    public void Save(IReadOnlyList<ExportJob> jobs)
    {
        var path = Location;
        if (path is null)
        {
            return;
        }

        lock (_ioGate)
        {
            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(directory))
            {
                LastError = "The job history path has no directory.";
                return;
            }

            var temporary = path + ".tmp";
            try
            {
                Directory.CreateDirectory(directory);
                var payload = new ExportJobFile
                {
                    Version = SchemaVersion,
                    SavedAt = DateTimeOffset.UtcNow,
                    Jobs = jobs?.Where(job => job is not null).ToList() ?? new List<ExportJob>()
                };

                File.WriteAllText(temporary, JsonSerializer.Serialize(payload, SerializerOptions));
                File.Move(temporary, path, overwrite: true);
                LastError = null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                LastError = $"Could not write {FileName}: {ex.Message}";
                try
                {
                    if (File.Exists(temporary))
                    {
                        File.Delete(temporary);
                    }
                }
                catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
                {
                    // Best effort: a leftover .tmp is harmless.
                }
            }
        }
    }

    private void QuarantineLocked(string path)
    {
        try
        {
            var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
            File.Move(path, Path.Combine(Path.GetDirectoryName(path)!, $"jobs.corrupt-{stamp}.json"), overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The history is already lost; keep the original message.
        }
    }

    private string? ResolveDirectory()
    {
        try
        {
            var directory = _directoryProvider();
            return string.IsNullOrWhiteSpace(directory) ? null : directory;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// On-disk document. The version is written so a future schema change can
    /// migrate the file instead of guessing from the job shape.
    /// </summary>
    private sealed class ExportJobFile
    {
        public int Version { get; set; }

        public DateTimeOffset SavedAt { get; set; }

        public List<ExportJob> Jobs { get; set; } = new();
    }
}
