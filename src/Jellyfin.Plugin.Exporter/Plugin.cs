using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.Exporter.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Exporter;

/// <summary>
/// Plugin entry point for Jellyfin Exporter.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Stable plugin identifier. Never change this: the dashboard, the plugin
    /// catalogue and saved configurations all key off it.
    /// </summary>
    public static readonly Guid PluginId = Guid.Parse("b0473c79-36c1-4386-9006-f941031bbea2");

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <inheritdoc />
    public override string Name => "Jellyfin Exporter";

    /// <inheritdoc />
    public override string Description =>
        "Export movies, shows and seasons to a folder using FFmpeg profiles (codec, resolution, audio channels).";

    /// <inheritdoc />
    public override Guid Id => PluginId;

    /// <summary>
    /// Gets the current plugin instance, if the server has created it.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            // ONE page for the whole plugin: browse/queue AND the profile
            // editor. Jellyfin's dashboard picks the Settings destination with
            // findBestConfigurationPage, which among a plugin's pages prefers
            // the first one flagged EnableInMainMenu; registering the profile
            // editor as its own page meant that check returned the browse page
            // and the settings button could never reach the profiles UI.
            new PluginPageInfo
            {
                Name = Name,
                DisplayName = "Exporter",
                EmbeddedResourcePath = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}.Web.exporterPage.html",
                    typeof(Plugin).Namespace),
                EnableInMainMenu = true,
                MenuIcon = "file_download"
            },

            // The status page the export page links to after queueing, and the
            // per-job progress/history surface. Kept out of the main menu so it
            // cannot compete with the merged page for the Settings link.
            new PluginPageInfo
            {
                Name = StatusPageName,
                DisplayName = "Export Status",
                EmbeddedResourcePath = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}.Web.statusPage.html",
                    typeof(Plugin).Namespace)
            }
        };
    }

    /// <summary>
    /// Route name of the export status page
    /// (<c>/web/ConfigurationPage?name=Export%20Status</c>).
    /// </summary>
    public const string StatusPageName = "Export Status";

    /// <summary>
    /// Rejects a configuration the settings page should never have saved (for
    /// example a relative output directory) so the API cannot store profiles
    /// the exporter could not use.
    /// </summary>
    /// <param name="configuration">Candidate configuration.</param>
    /// <exception cref="ArgumentException">Thrown when the configuration has validation issues.</exception>
    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        if (configuration is PluginConfiguration candidate)
        {
            var issues = PluginConfigurationValidator.Validate(candidate);
            if (issues.Count > 0)
            {
                throw new ArgumentException(
                    "Export configuration rejected: " + string.Join(" ", issues.Select(i => i.Message)),
                    nameof(configuration));
            }
        }

        base.UpdateConfiguration(configuration);
    }
}
