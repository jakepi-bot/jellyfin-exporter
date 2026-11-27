using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.Exporter.Export;
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
    /// <param name="transcoding">
    /// Jellyfin's current transcoding settings; when given, profiles that
    /// honour them are checked against Jellyfin's allowed codecs
    /// (<c>AllowHevcEncoding</c>/<c>AllowAv1Encoding</c>). Null skips that
    /// check (used where the encoding options are not reachable, e.g.
    /// <c>Plugin.UpdateConfiguration</c>).
    /// </param>
    /// <returns>All problems found; empty when the configuration may be saved.</returns>
    public static IReadOnlyList<ConfigurationIssue> Validate(
        PluginConfiguration? configuration,
        IOutputDirectoryInspector? inspector = null,
        JellyfinTranscodingSettings? transcoding = null)
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

        if (configuration.JobHistoryLimit < 1 ||
            configuration.JobHistoryLimit > PluginConfiguration.JobHistoryLimitMaximum)
        {
            issues.Add(new ConfigurationIssue(
                null,
                nameof(PluginConfiguration.JobHistoryLimit),
                $"Job history must keep between 1 and {PluginConfiguration.JobHistoryLimitMaximum} finished jobs."));
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

            if (transcoding is not null &&
                profile.UseJellyfinTranscodingSettings &&
                !transcoding.Allows(profile.Format))
            {
                issues.Add(new ConfigurationIssue(
                    profile.Id,
                    nameof(ExportProfile.Format),
                    FormatBlockedMessage(profile, transcoding)));
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

    /// <summary>
    /// The operator-facing message for a profile whose format Jellyfin's
    /// transcoding settings do not allow. Used both when the settings page
    /// validates a save and when the plan refuses to queue the profile.
    /// </summary>
    /// <param name="profile">The blocked profile.</param>
    /// <param name="transcoding">Jellyfin's current transcoding settings.</param>
    /// <returns>Message naming the Jellyfin switch to flip.</returns>
    public static string FormatBlockedMessage(ExportProfile profile, JellyfinTranscodingSettings transcoding)
    {
        var isAv1 = profile.Format == ExportFormat.AV1;
        var codec = isAv1 ? "AV1" : "HEVC";
        var label = isAv1 ? "AV1" : "H.265";
        var jellyfinSwitch = isAv1 ? "Allow AV1 encoding" : "Allow HEVC encoding";

        return string.Format(
            CultureInfo.InvariantCulture,
            "Jellyfin's transcoding settings have {0} encoding disabled (Dashboard > Playback, '{1}'), so profile '{2}' cannot export {3}. Enable it there, choose another format, or turn off \"Use Jellyfin's transcoding settings\" for this profile.",
            codec,
            jellyfinSwitch,
            profile.Name,
            label);
    }
}
