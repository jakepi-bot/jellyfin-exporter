using System;
using Jellyfin.Plugin.Exporter.Export;
using MediaBrowser.Common.Configuration;

namespace Jellyfin.Plugin.Exporter.Services;

/// <summary>
/// Provides the current Jellyfin transcoding settings the exporter honours
/// (hardware switch, allowed codecs, preset, CRFs, thread count).
/// </summary>
public interface ITranscodingSettingsSource
{
    /// <summary>Gets the settings as they are right now.</summary>
    JellyfinTranscodingSettings Current { get; }
}

/// <summary>
/// Reads Jellyfin's own <c>EncodingOptions</c> (Dashboard &gt; Playback) on
/// every access, so a change there applies to the next export without a
/// restart. Any read failure falls back to Jellyfin's defaults instead of
/// breaking an export.
/// </summary>
public sealed class JellyfinTranscodingSettingsSource : ITranscodingSettingsSource
{
    private readonly IConfigurationManager _configuration;

    /// <summary>
    /// Initializes a new instance of the <see cref="JellyfinTranscodingSettingsSource"/> class.
    /// </summary>
    /// <param name="configuration">Server configuration manager (provides <c>GetEncodingOptions</c>).</param>
    public JellyfinTranscodingSettingsSource(IConfigurationManager configuration)
    {
        _configuration = configuration;
    }

    /// <inheritdoc />
    public JellyfinTranscodingSettings Current
    {
        get
        {
            try
            {
                var options = _configuration.GetEncodingOptions();
                return new JellyfinTranscodingSettings(
                    options.EnableHardwareEncoding,
                    options.AllowHevcEncoding,
                    options.AllowAv1Encoding,
                    options.EncoderPreset.ToString(),
                    options.H264Crf,
                    options.H265Crf,
                    options.EncodingThreadCount);
            }
            catch (Exception)
            {
                return JellyfinTranscodingSettings.Default;
            }
        }
    }
}
