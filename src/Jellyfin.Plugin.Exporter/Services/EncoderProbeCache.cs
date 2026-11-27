using System;
using System.Collections.Concurrent;

namespace Jellyfin.Plugin.Exporter.Services;

/// <summary>
/// Caches one-frame hardware encoder probe results per process, keyed by
/// (device, encoder). Detection runs the exact-encoder test for every format
/// and, on a multi-GPU host, for every render node — the cache keeps a server
/// restart the only thing that re-runs FFmpeg.
/// </summary>
public sealed class EncoderProbeCache
{
    private readonly ConcurrentDictionary<string, bool> _results = new(StringComparer.Ordinal);

    /// <summary>
    /// Runs (or returns the cached result of) a probe for one device/encoder
    /// pair. Exceptions from <paramref name="probe"/> propagate uncaught, so
    /// the caller decides whether a throwing probe means "unusable".
    /// </summary>
    /// <param name="device">Render device path, or null when the encoder takes no device.</param>
    /// <param name="encoder">Exact FFmpeg encoder name, e.g. <c>av1_vaapi</c>.</param>
    /// <param name="probe">The real one-frame test encode.</param>
    /// <returns>Whether the encode succeeded.</returns>
    public bool Get(string? device, string encoder, Func<string?, string, bool> probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(encoder);

        var key = string.Concat(device ?? string.Empty, "\u001f", encoder);
        return _results.GetOrAdd(key, _ => probe(device, encoder));
    }
}
