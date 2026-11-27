using Jellyfin.Plugin.Exporter.Api;
using Jellyfin.Plugin.Exporter.Configuration;
using Jellyfin.Plugin.Exporter.Export;
using Jellyfin.Plugin.Exporter.Models;
using Jellyfin.Plugin.Exporter.Services;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Jellyfin.Plugin.Exporter.Tests;

/// <summary>
/// The save-time half of the codec gate: the settings page posts its payload
/// to Profiles/Validate before saving, and the endpoint has to read Jellyfin's
/// transcoding settings to refuse a format Jellyfin disallows.
/// </summary>
public class ExporterProfilesControllerTests
{
    /// <summary>Probe stub so the rules can be tested without touching the file system.</summary>
    private sealed class StubInspector : IOutputDirectoryInspector
    {
        public DirectoryProbeResult Probe(string? path) => new(true, string.Empty, false);
    }

    private sealed class FixedTranscoding : ITranscodingSettingsSource
    {
        public FixedTranscoding(JellyfinTranscodingSettings settings) => Current = settings;

        public JellyfinTranscodingSettings Current { get; }
    }

    private static ConfigurationPayload Payload(bool formatAv1, bool? useJellyfinSettings = null) => new()
    {
        Profiles = new List<ProfilePayload>
        {
            new()
            {
                Id = "11111111-1111-1111-1111-111111111111",
                Name = "Phone copy",
                Format = formatAv1 ? "AV1" : "H264",
                Resolution = "Resolution480p",
                AudioChannels = "Stereo",
                OutputDirectory = "/media/exports",
                Enabled = true,
                UseJellyfinTranscodingSettings = useJellyfinSettings
            }
        },
        MaxConcurrentExports = 1
    };

    private static ConfigurationValidationResult Validate(ConfigurationPayload payload, JellyfinTranscodingSettings settings)
    {
        var controller = new ExporterProfilesController(new StubInspector(), new FixedTranscoding(settings));
        var result = controller.Validate(payload);
        Assert.Null(result.Result);
        return Assert.IsType<ConfigurationValidationResult>(result.Value);
    }

    [Fact]
    public void Validate_DefaultsTheTranscodingToggleToTrueWhenThePayloadOmitsIt()
    {
        var configuration = Payload(formatAv1: true).ToConfiguration();

        Assert.True(configuration.Profiles[0].UseJellyfinTranscodingSettings);
    }

    [Fact]
    public void Validate_ReadsTheTranscodingToggleFromThePayload()
    {
        var configuration = Payload(formatAv1: true, useJellyfinSettings: false).ToConfiguration();

        Assert.False(configuration.Profiles[0].UseJellyfinTranscodingSettings);
    }

    [Fact]
    public void Validate_BlocksAnAv1ProfileJellyfinDisallows()
    {
        var result = Validate(
            Payload(formatAv1: true),
            new JellyfinTranscodingSettings(AllowAv1Encoding: false));

        Assert.False(result.Valid);
        var issue = Assert.Single(result.Issues);
        Assert.Equal("Format", issue.Field);
        Assert.Contains("AV1", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_AcceptsAnAv1ProfileWhenTheProfileOptsOutOfJellyfinSettings()
    {
        var result = Validate(
            Payload(formatAv1: true, useJellyfinSettings: false),
            new JellyfinTranscodingSettings(AllowAv1Encoding: false));

        Assert.True(result.Valid, string.Join(" ", result.Issues.Select(i => i.Message)));
    }

    [Fact]
    public void Validate_AcceptsAnAv1ProfileWhenJellyfinAllowsAv1()
    {
        var result = Validate(
            Payload(formatAv1: true),
            new JellyfinTranscodingSettings(AllowAv1Encoding: true));

        Assert.True(result.Valid, string.Join(" ", result.Issues.Select(i => i.Message)));
    }

    [Fact]
    public void Validate_AcceptsAnH264ProfileRegardlessOfTheCodecSwitches()
    {
        var result = Validate(
            Payload(formatAv1: false),
            new JellyfinTranscodingSettings(AllowHevcEncoding: false, AllowAv1Encoding: false));

        Assert.True(result.Valid, string.Join(" ", result.Issues.Select(i => i.Message)));
    }
}
