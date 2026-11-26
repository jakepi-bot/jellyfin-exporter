using System;
using Jellyfin.Plugin.Exporter.Models;

namespace Jellyfin.Plugin.Exporter.Services;

/// <summary>
/// Raised by <see cref="IExportQueue"/> whenever a job changes status.
/// </summary>
public sealed class ExportJobStatusChangedEventArgs : EventArgs
{
    public ExportJobStatusChangedEventArgs(ExportJob job, ExportJobStatus previousStatus)
    {
        Job = job ?? throw new ArgumentNullException(nameof(job));
        PreviousStatus = previousStatus;
    }

    /// <summary>
    /// Gets the job in its new state.
    /// </summary>
    public ExportJob Job { get; }

    /// <summary>
    /// Gets the status the job had before the change.
    /// </summary>
    public ExportJobStatus PreviousStatus { get; }
}
