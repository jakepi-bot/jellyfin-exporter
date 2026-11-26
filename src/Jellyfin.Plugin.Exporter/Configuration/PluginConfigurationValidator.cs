using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Exporter.Models;

namespace Jellyfin.Plugin.Exporter.Configuration;

/// <summary>
/// One problem with a candidate configuration, addressed to the field the
/// operator has to fix.
/// </summary>
/// <param name="ProfileId">Profile the issue belongs to, or null for global settings.</param>
/// <param name="Field">Field name the settings page highlights: Name, OutputDirectory, DefaultProfileId, MaxConcurrentExports, Profiles.</param>
/// <param name="Message">Operator-facing message.</param>
public sealed record ConfigurationIssue(Guid? ProfileId, string Field, string Message);

/// <summary>
/// Validates a candidate <see cref="PluginConfiguration"/> before it is saved.
/// Rules are pure except for the output-directory probe, which is injected so
/// the validation logic stays unit-testable.
/// </summary>
public static class PluginConfigurationValidator
{
    /// <summary>Maximum profile name length.</summary>
    public const int MaxProfileNameLength = 64;

    /// <summary>
    /// Validates the whole configuration.
    /// </summary>
    /// <param name="configuration">Candidate configuration.</param>
    /// <param name="inspector">Directory probe; defaults to the real file system.</param>
    /// <returns>All problems found; empty when the configuration may be saved.</returns>
    public static IReadOnlyList<ConfigurationIssue> Validate(
        PluginConfiguration? configuration,
        IOutputDirectoryInspector? inspector = null)
    {
        var issues = new List<ConfigurationIssue>();
        if (configuration is null)
        {
            issues.Add(new ConfigurationIssue(null, "Profiles", "The configuration payload is empty."));
            return issues;
        }

        inspector ??= new OutputDirectoryInspector();

        if (configuration.MaxConcurrentExports < 1 ||
            configuration.MaxConcurrentExports > PluginConfiguration.MaxConcurrentExportsLimit)
        {
            issues.Add(new ConfigurationIssue(
                null,
                nameof(PluginConfiguration.MaxConcurrentExports),
                $"Max concurrent exports must be between 1 and {PluginConfiguration.MaxConcurrentExportsLimit}."));
        }

        var profiles = configuration.Profiles ?? new List<ExportProfile>();
        var seenIds = new HashSet<Guid>();
        var seenNames = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < profiles.Count; index++)
        {
            var profile = profiles[index];
            if (profile is null)
            {
                issues.Add(new ConfigurationIssue(null, "Profiles", $"Profile #{index + 1} is empty."));
                continue;
            }

            if (!seenIds.Add(profile.Id))
            {
                issues.Add(new ConfigurationIssue(
                    profile.Id,
                    nameof(ExportProfile.Id),
                    "This profile id is used by more than one profile."));
            }

            var name = (profile.Name ?? string.Empty).Trim();
            if (name.Length == 0)
            {
                issues.Add(new ConfigurationIssue(profile.Id, nameof(ExportProfile.Name), "A profile name is required."));
            }
            else if (name.Length > MaxProfileNameLength)
            {
                issues.Add(new ConfigurationIssue(
                    profile.Id,
                    nameof(ExportProfile.Name),
                    $"Profile names are limited to {MaxProfileNameLength} characters."));
            }
            else if (seenNames.TryGetValue(name, out var firstIndex))
            {
                issues.Add(new ConfigurationIssue(
                    profile.Id,
                    nameof(ExportProfile.Name),
                    $"Profile names must be unique; this one duplicates profile #{firstIndex + 1}."));
            }
            else
            {
                seenNames[name] = index;
            }

            var probe = inspector.Probe(profile.OutputDirectory);
            if (!probe.IsValid)
            {
                issues.Add(new ConfigurationIssue(profile.Id, nameof(ExportProfile.OutputDirectory), probe.Message));
            }
        }

        if (configuration.DefaultProfileId is Guid defaultId && configuration.GetProfile(defaultId) is null)
        {
            issues.Add(new ConfigurationIssue(
                null,
                nameof(PluginConfiguration.DefaultProfileId),
                "The default profile no longer exists. Choose another default profile or clear the default."));
        }

        return issues;
    }
}
