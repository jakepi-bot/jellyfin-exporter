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
            new PluginPageInfo
            {
                Name = Name,
                DisplayName = Name,
                EmbeddedResourcePath = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}.Web.configPage.html",
                    typeof(Plugin).Namespace)
            },

            // The browse/queue page. EnableInMainMenu puts "Exporter" in the
            // dashboard menu instead of hiding it behind the plugin settings.
            new PluginPageInfo
            {
                Name = BrowsePageName,
                DisplayName = "Exporter",
                EmbeddedResourcePath = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}.Web.exporterPage.html",
                    typeof(Plugin).Namespace),
                EnableInMainMenu = true,
                MenuIcon = "file_download"
            }
        };
    }

    /// <summary>
    /// Route name of the browse/queue page
    /// (<c>/web/ConfigurationPage?name=Exporter</c>).
    /// </summary>
    public const string BrowsePageName = "Exporter";

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
