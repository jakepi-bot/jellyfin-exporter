using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.Exporter.Configuration;
using Jellyfin.Plugin.Exporter.Models;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Exporter.Api;

/// <summary>
/// Validation endpoints used by the settings page. Directory checks have to run
/// on the server: the dashboard cannot see the server's file system.
/// </summary>
/// <remarks>
/// Bodies are read as <see cref="JsonElement"/> and bound with explicit options
/// because the plugin configuration endpoint and the dashboard do not agree on
/// property casing between Jellyfin releases.
/// </remarks>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("JellyfinExporter/Profiles")]
public class ExporterProfilesController : ControllerBase
{
    private static readonly JsonSerializerOptions IncomingOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IOutputDirectoryInspector _inspector;

    /// <summary>
    /// Initializes a new instance of the <see cref="ExporterProfilesController"/> class.
    /// Used when the plugin's services are not registered yet.
    /// </summary>
    public ExporterProfilesController()
        : this(new OutputDirectoryInspector())
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ExporterProfilesController"/> class.
    /// </summary>
    /// <param name="inspector">Directory probe.</param>
    public ExporterProfilesController(IOutputDirectoryInspector inspector)
    {
        _inspector = inspector;
    }

    /// <summary>
    /// Validates a candidate configuration before the settings page saves it.
    /// </summary>
    /// <param name="payload">The configuration the page is about to save.</param>
    /// <returns>Per-field issues; empty when the configuration is savable.</returns>
    [HttpPost("Validate")]
    public ActionResult<ConfigurationValidationResult> Validate([FromBody] ConfigurationPayload payload)
    {
        if (payload is null)
        {
            return BadRequest(new ConfigurationValidationResult
            {
                Valid = false,
                Issues = new[]
                {
                    new ConfigurationIssueResponse(null, "Profiles", "The configuration payload is empty.")
                }
            });
        }

        var configuration = payload.ToConfiguration();
        var issues = PluginConfigurationValidator.Validate(configuration, _inspector);
        return new ConfigurationValidationResult
        {
            Valid = issues.Count == 0,
            Issues = issues.Select(i => new ConfigurationIssueResponse(i.ProfileId, i.Field, i.Message)).ToArray()
        };
    }

    /// <summary>
    /// Probes a single output directory so the settings page can report a
    /// problem while the operator is still typing.
    /// </summary>
    /// <param name="body">Object with the candidate path, usually {"path": "..."}.</param>
    /// <returns>Whether the exporter could write there.</returns>
    [HttpPost("ProbeDirectory")]
    public ActionResult<DirectoryProbeResponse> ProbeDirectory([FromBody] JsonElement body)
    {
        var path = ReadPath(body);
        var probe = _inspector.Probe(path);
        return new DirectoryProbeResponse
        {
            Valid = probe.IsValid,
            WillBeCreated = probe.WillBeCreated,
            Message = probe.Message
        };
    }

    private static string? ReadPath(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var property in body.EnumerateObject())
        {
            if (string.Equals(property.Name, "path", StringComparison.OrdinalIgnoreCase) &&
                property.Value.ValueKind == JsonValueKind.String)
            {
                return property.Value.GetString();
            }
        }

        return null;
    }
}

/// <summary>
/// Candidate configuration posted by the settings page.
/// </summary>
/// <remarks>
/// Ids arrive as strings because the plugin configuration endpoint serializes
/// <see cref="Guid"/> without dashes, which <see cref="JsonSerializer"/> cannot
/// read back. Parsing them here keeps both forms accepted.
/// </remarks>
public class ConfigurationPayload
{
    /// <summary>Gets or sets the profiles.</summary>
    public List<ProfilePayload>? Profiles { get; set; }

    /// <summary>Gets or sets the default profile id.</summary>
    public string? DefaultProfileId { get; set; }

    /// <summary>Gets or sets the concurrent export limit.</summary>
    public int? MaxConcurrentExports { get; set; }

    /// <summary>Gets or sets how many finished jobs the status page keeps.</summary>
    public int? JobHistoryLimit { get; set; }

    /// <summary>Gets or sets a value indicating whether completion notifications are sent.</summary>
    public bool? NotifyOnCompletion { get; set; }

    /// <summary>Gets or sets a value indicating whether failure notifications are sent.</summary>
    public bool? NotifyOnFailure { get; set; }

    /// <summary>
    /// Maps the payload onto the validated configuration model.
    /// </summary>
    /// <returns>The configuration to validate.</returns>
    public PluginConfiguration ToConfiguration()
    {
        var configuration = new PluginConfiguration
        {
            MaxConcurrentExports = MaxConcurrentExports ?? 1,
            JobHistoryLimit = JobHistoryLimit ?? new PluginConfiguration().JobHistoryLimit,
            NotifyOnCompletion = NotifyOnCompletion ?? true,
            NotifyOnFailure = NotifyOnFailure ?? true,
            DefaultProfileId = ParseGuid(DefaultProfileId),
            Profiles = new List<ExportProfile>()
        };

        foreach (var profile in Profiles ?? new List<ProfilePayload>())
        {
            if (profile is null)
            {
                continue;
            }

            configuration.Profiles.Add(new ExportProfile
            {
                Id = ParseGuid(profile.Id) ?? Guid.NewGuid(),
                Name = profile.Name ?? string.Empty,
                Format = ParseEnum(profile.Format, ExportFormat.H264),
                Resolution = ParseEnum(profile.Resolution, ExportResolution.Resolution1080p),
                AudioChannels = ParseEnum(profile.AudioChannels, ExportAudioChannels.Original),
                OutputDirectory = profile.OutputDirectory ?? string.Empty,
                Enabled = profile.Enabled ?? true
            });
        }

        return configuration;
    }

    private static Guid? ParseGuid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Guid.TryParse(value, out var guid) || guid == Guid.Empty)
        {
            return null;
        }

        return guid;
    }

    private static TEnum ParseEnum<TEnum>(string? value, TEnum fallback)
        where TEnum : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        if (Enum.TryParse<TEnum>(value, true, out var parsed))
        {
            return parsed;
        }

        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) &&
            Enum.IsDefined(typeof(TEnum), number))
        {
            return (TEnum)Enum.ToObject(typeof(TEnum), number);
        }

        return fallback;
    }
}

/// <summary>
/// One profile in a <see cref="ConfigurationPayload"/>.
/// </summary>
public class ProfilePayload
{
    /// <summary>Gets or sets the profile id.</summary>
    public string? Id { get; set; }

    /// <summary>Gets or sets the profile name.</summary>
    public string? Name { get; set; }

    /// <summary>Gets or sets the codec.</summary>
    public string? Format { get; set; }

    /// <summary>Gets or sets the resolution.</summary>
    public string? Resolution { get; set; }

    /// <summary>Gets or sets the audio handling.</summary>
    public string? AudioChannels { get; set; }

    /// <summary>Gets or sets the output directory.</summary>
    public string? OutputDirectory { get; set; }

    /// <summary>Gets or sets a value indicating whether the profile can queue exports.</summary>
    public bool? Enabled { get; set; }
}

/// <summary>
/// Response for <see cref="ExporterProfilesController.Validate"/>.
/// </summary>
public class ConfigurationValidationResult
{
    /// <summary>Gets or sets a value indicating whether the configuration may be saved.</summary>
    public bool Valid { get; set; }

    /// <summary>Gets or sets the issues found.</summary>
    public IReadOnlyList<ConfigurationIssueResponse> Issues { get; set; } = Array.Empty<ConfigurationIssueResponse>();
}

/// <summary>
/// One validation issue, addressed to a field the page can highlight.
/// </summary>
public class ConfigurationIssueResponse
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ConfigurationIssueResponse"/> class.
    /// </summary>
    /// <param name="profileId">Profile id, or null for global settings.</param>
    /// <param name="field">Field name.</param>
    /// <param name="message">Message for the operator.</param>
    public ConfigurationIssueResponse(Guid? profileId, string field, string message)
    {
        ProfileId = profileId;
        Field = field;
        Message = message;
    }

    /// <summary>Gets or sets the profile id, or null for a global setting.</summary>
    public Guid? ProfileId { get; set; }

    /// <summary>Gets or sets the field name (Name, OutputDirectory, DefaultProfileId, MaxConcurrentExports, Profiles).</summary>
    public string Field { get; set; }

    /// <summary>Gets or sets the operator-facing message.</summary>
    public string Message { get; set; }
}

/// <summary>
/// Response for <see cref="ExporterProfilesController.ProbeDirectory"/>.
/// </summary>
public class DirectoryProbeResponse
{
    /// <summary>Gets or sets a value indicating whether the directory is usable.</summary>
    public bool Valid { get; set; }

    /// <summary>Gets or sets a value indicating whether the directory will be created on first export.</summary>
    public bool WillBeCreated { get; set; }

    /// <summary>Gets or sets the message to show when the directory is not usable.</summary>
    public string Message { get; set; } = string.Empty;
}
