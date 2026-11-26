using System;

namespace Jellyfin.Plugin.Exporter.Export;

/// <summary>
/// A failure the operator can act on: a missing source file, an unusable
/// directory, or an FFmpeg invocation that could not be built. The message is
/// stored on the job as its error message.
/// </summary>
public sealed class ExportException : Exception
{
    public ExportException()
    {
    }

    public ExportException(string message)
        : base(message)
    {
    }

    public ExportException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
