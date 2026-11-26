using System.Collections.Generic;
using Jellyfin.Plugin.Exporter.Models;

namespace Jellyfin.Plugin.Exporter.Services;

/// <summary>
/// Durable home for the queue's job history. The queue writes a snapshot after
/// every status transition so the status page still knows what happened after a
/// server restart.
/// </summary>
/// <remarks>
/// Implementations must never throw out of <see cref="Save"/>: a full disk or a
/// locked file may not take the export queue down. Problems are reported
/// through <see cref="LastError"/> instead.
/// </remarks>
public interface IExportJobStore
{
    /// <summary>
    /// Gets the absolute path the jobs are stored at, or null when the store
    /// has no location (in-memory).
    /// </summary>
    string? Location { get; }

    /// <summary>
    /// Gets the last load or save problem, or null when the last operation
    /// succeeded.
    /// </summary>
    string? LastError { get; }

    /// <summary>
    /// Reads the stored jobs.
    /// </summary>
    /// <returns>The stored jobs; empty when nothing has been stored yet.</returns>
    IReadOnlyList<ExportJob> Load();

    /// <summary>
    /// Replaces the stored snapshot.
    /// </summary>
    /// <param name="jobs">Jobs to store.</param>
    void Save(IReadOnlyList<ExportJob> jobs);
}

/// <summary>
/// Store used by unit tests and by the parameterless constructors: nothing is
/// persisted and nothing is restored.
/// </summary>
public sealed class NullExportJobStore : IExportJobStore
{
    /// <summary>
    /// A shared instance; the store is stateless.
    /// </summary>
    public static readonly NullExportJobStore Instance = new();

    /// <inheritdoc />
    public string? Location => null;

    /// <inheritdoc />
    public string? LastError => null;

    /// <inheritdoc />
    public IReadOnlyList<ExportJob> Load() => new List<ExportJob>();

    /// <inheritdoc />
    public void Save(IReadOnlyList<ExportJob> jobs)
    {
        // Nothing to persist.
    }
}
