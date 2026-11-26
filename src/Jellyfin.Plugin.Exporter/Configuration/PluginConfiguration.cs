using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Serialization;
using Jellyfin.Plugin.Exporter.Models;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Exporter.Configuration;

/// <summary>
/// Persisted plugin configuration (stored by Jellyfin as an XML file next to the
/// plugin data folder).
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Highest value accepted for <see cref="MaxConcurrentExports"/>.
    /// </summary>
    public const int MaxConcurrentExportsLimit = 8;

    public PluginConfiguration()
    {
        Profiles = new List<ExportProfile>();
        MaxConcurrentExports = 1;
        NotifyOnCompletion = true;
        NotifyOnFailure = true;
    }

    /// <summary>
    /// Gets or sets the configured export profiles. Must be a concrete
    /// <see cref="List{T}"/> because Jellyfin persists configuration through
    /// <see cref="XmlSerializer"/>.
    /// </summary>
    public List<ExportProfile> Profiles { get; set; }

    /// <summary>
    /// Gets or sets the profile pre-selected in the export UI. Null means
    /// "first enabled profile".
    /// </summary>
    public Guid? DefaultProfileId { get; set; }

    /// <summary>
    /// Gets or sets how many FFmpeg exports may run at the same time.
    /// </summary>
    public int MaxConcurrentExports { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a Jellyfin notification is
    /// raised when an export completes.
    /// </summary>
    public bool NotifyOnCompletion { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a Jellyfin notification is
    /// raised when an export fails.
    /// </summary>
    public bool NotifyOnFailure { get; set; }

    /// <summary>
    /// Finds a profile by id.
    /// </summary>
    public ExportProfile? GetProfile(Guid id) => Profiles.FirstOrDefault(p => p.Id == id);

    /// <summary>
    /// Gets the default profile, falling back to the first enabled profile and
    /// then to the first configured profile.
    /// </summary>
    public ExportProfile? GetDefaultProfile()
    {
        if (DefaultProfileId is Guid id)
        {
            var match = GetProfile(id);
            if (match is not null)
            {
                return match;
            }
        }

        return Profiles.FirstOrDefault(p => p.Enabled) ?? Profiles.FirstOrDefault();
    }

    /// <summary>
    /// Gets the profiles that may be used to enqueue new jobs.
    /// </summary>
    public IEnumerable<ExportProfile> GetEnabledProfiles() => Profiles.Where(p => p.Enabled);
}
