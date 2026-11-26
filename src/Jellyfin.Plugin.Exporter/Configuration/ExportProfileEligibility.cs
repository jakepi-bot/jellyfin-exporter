using System;
using System.Linq;
using Jellyfin.Plugin.Exporter.Models;

namespace Jellyfin.Plugin.Exporter.Configuration;

/// <summary>
/// Outcome of checking whether a profile may queue new exports.
/// </summary>
/// <param name="Profile">The profile, when it may be used.</param>
/// <param name="ErrorMessage">Why it may not be used; null when it may.</param>
public sealed record ProfileEligibility(ExportProfile? Profile, string? ErrorMessage)
{
    /// <summary>
    /// Gets a value indicating whether the profile may queue exports.
    /// </summary>
    public bool IsUsable => Profile is not null && ErrorMessage is null;
}

/// <summary>
/// Decides whether a configured profile can queue export jobs: it has to exist,
/// be enabled, and have an output directory the exporter could write to.
/// </summary>
public static class ExportProfileEligibility
{
    /// <summary>
    /// Evaluates one profile id against the current configuration.
    /// </summary>
    /// <param name="configuration">Current plugin configuration.</param>
    /// <param name="profileId">Profile the operator chose.</param>
    /// <param name="inspector">Directory probe; defaults to the real file system.</param>
    /// <returns>The profile when usable, otherwise the operator-facing reason.</returns>
    public static ProfileEligibility Evaluate(
        PluginConfiguration? configuration,
        Guid profileId,
        IOutputDirectoryInspector? inspector = null)
    {
        if (configuration is null)
        {
            return new ProfileEligibility(null, "The exporter configuration is not available yet.");
        }

        if (profileId == Guid.Empty)
        {
            return new ProfileEligibility(null, "Choose an export profile.");
        }

        var profile = configuration.GetProfile(profileId);
        if (profile is null)
        {
            return new ProfileEligibility(
                null,
                "That export profile no longer exists. Reload the page and pick another one.");
        }

        if (!profile.Enabled)
        {
            return new ProfileEligibility(
                profile,
                $"The profile '{profile.Name}' is disabled in the plugin settings, so it cannot queue exports.");
        }

        var issues = PluginConfigurationValidator
            .Validate(configuration, inspector)
            .Where(issue => issue.ProfileId == profile.Id)
            .Select(issue => issue.Message)
            .ToArray();

        if (issues.Length > 0)
        {
            return new ProfileEligibility(
                profile,
                $"The profile '{profile.Name}' is not usable: {string.Join(" ", issues)}");
        }

        return new ProfileEligibility(profile, null);
    }
}
