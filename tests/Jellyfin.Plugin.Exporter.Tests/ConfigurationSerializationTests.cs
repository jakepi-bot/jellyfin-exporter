using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Serialization;
using Jellyfin.Plugin.Exporter.Configuration;
using Jellyfin.Plugin.Exporter.Models;
using Xunit;

namespace Jellyfin.Plugin.Exporter.Tests;

/// <summary>
/// Jellyfin stores plugin configuration with <see cref="XmlSerializer"/> and
/// hands it to the dashboard as JSON; both representations must survive a
/// round-trip, otherwise profiles silently change after a restart.
/// </summary>
public class ConfigurationSerializationTests
{
    private static readonly Guid ProfileA = Guid.Parse("6f1f6b64-3a4e-4c31-9a6f-6a1f8c0f0001");
    private static readonly Guid ProfileB = Guid.Parse("6f1f6b64-3a4e-4c31-9a6f-6a1f8c0f0002");

    private static PluginConfiguration Sample()
    {
        return new PluginConfiguration
        {
            MaxConcurrentExports = 3,
            NotifyOnCompletion = true,
            NotifyOnFailure = false,
            DefaultProfileId = ProfileB,
            Profiles = new List<ExportProfile>
            {
                new ExportProfile
                {
                    Id = ProfileA,
                    Name = "Archive 1080p",
                    Format = ExportFormat.H265,
                    Resolution = ExportResolution.Resolution1080p,
                    AudioChannels = ExportAudioChannels.Original,
                    OutputDirectory = "/media/exports",
                    Enabled = true,
                    UseJellyfinTranscodingSettings = true
                },
                new ExportProfile
                {
                    Id = ProfileB,
                    Name = "Phone copy",
                    Format = ExportFormat.AV1,
                    Resolution = ExportResolution.Resolution720p,
                    AudioChannels = ExportAudioChannels.Stereo,
                    OutputDirectory = "/srv/phone",
                    Enabled = false,
                    UseJellyfinTranscodingSettings = false
                }
            }
        };
    }

    [Fact]
    public void XmlRoundTrip_PreservesEveryField()
    {
        var original = Sample();
        var serializer = new XmlSerializer(typeof(PluginConfiguration));

        string xml;
        using (var writer = new StringWriter())
        {
            serializer.Serialize(writer, original);
            xml = writer.ToString();
        }

        // Jellyfin writes this file by hand-editing too, so the element names are part of the contract.
        Assert.Contains("<Profiles>", xml, StringComparison.Ordinal);
        Assert.Contains("<ExportProfile>", xml, StringComparison.Ordinal);
        Assert.Contains("<Format>H265</Format>", xml, StringComparison.Ordinal);
        Assert.Contains("<DefaultProfileId>", xml, StringComparison.Ordinal);

        PluginConfiguration? restored;
        using (var reader = new StringReader(xml))
        {
            restored = (PluginConfiguration?)serializer.Deserialize(reader);
        }

        Assert.NotNull(restored);
        Assert.Equal(3, restored!.MaxConcurrentExports);
        Assert.True(restored.NotifyOnCompletion);
        Assert.False(restored.NotifyOnFailure);
        Assert.Equal(ProfileB, restored.DefaultProfileId);
        Assert.Equal(2, restored.Profiles.Count);

        Assert.Equal(ProfileA, restored.Profiles[0].Id);
        Assert.Equal("Archive 1080p", restored.Profiles[0].Name);
        Assert.Equal(ExportFormat.H265, restored.Profiles[0].Format);
        Assert.Equal(ExportResolution.Resolution1080p, restored.Profiles[0].Resolution);
        Assert.Equal(ExportAudioChannels.Original, restored.Profiles[0].AudioChannels);
        Assert.Equal("/media/exports", restored.Profiles[0].OutputDirectory);
        Assert.True(restored.Profiles[0].Enabled);
        Assert.True(restored.Profiles[0].UseJellyfinTranscodingSettings);
        Assert.False(restored.Profiles[1].UseJellyfinTranscodingSettings);

        Assert.Equal(ExportFormat.AV1, restored.Profiles[1].Format);
        Assert.Equal(ExportResolution.Resolution720p, restored.Profiles[1].Resolution);
        Assert.Equal(ExportAudioChannels.Stereo, restored.Profiles[1].AudioChannels);
        Assert.False(restored.Profiles[1].Enabled);
    }

    [Fact]
    public void XmlWithoutTheTranscodingToggle_DefaultsToHonouringJellyfinSettings()
    {
        // Configs saved by older plugin versions have no such element; the
        // toggle must come back TRUE (the documented default).
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <PluginConfiguration xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
              <MaxConcurrentExports>1</MaxConcurrentExports>
              <Profiles>
                <ExportProfile>
                  <Id>6f1f6b64-3a4e-4c31-9a6f-6a1f8c0f0001</Id>
                  <Name>Legacy</Name>
                  <Format>H264</Format>
                  <Resolution>Resolution1080p</Resolution>
                  <AudioChannels>Original</AudioChannels>
                  <OutputDirectory>/media/exports</OutputDirectory>
                  <Enabled>true</Enabled>
                </ExportProfile>
              </Profiles>
            </PluginConfiguration>
            """;

        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        using var reader = new StringReader(xml);
        var restored = (PluginConfiguration?)serializer.Deserialize(reader);

        Assert.NotNull(restored);
        Assert.True(restored!.Profiles[0].UseJellyfinTranscodingSettings);
    }

    [Fact]
    public void XmlRoundTrip_WithNoDefaultProfile_KeepsDefaultNull()
    {
        var original = Sample();
        original.DefaultProfileId = null;
        var serializer = new XmlSerializer(typeof(PluginConfiguration));

        using var writer = new StringWriter();
        serializer.Serialize(writer, original);
        using var reader = new StringReader(writer.ToString());
        var restored = (PluginConfiguration?)serializer.Deserialize(reader);

        Assert.NotNull(restored);
        Assert.Null(restored!.DefaultProfileId);
    }

    [Fact]
    public void JsonRoundTrip_MatchesTheDashboardContract()
    {
        // Mirrors the dashboard's serializer: camelCase members, enum member names.
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter() }
        };

        var original = Sample();
        var json = JsonSerializer.Serialize(original, options);

        Assert.Contains("\"profiles\"", json, StringComparison.Ordinal);
        Assert.Contains("\"defaultProfileId\"", json, StringComparison.Ordinal);
        Assert.Contains("\"maxConcurrentExports\":3", json, StringComparison.Ordinal);
        Assert.Contains("\"format\":\"AV1\"", json, StringComparison.Ordinal);
        Assert.Contains("\"audioChannels\":\"Stereo\"", json, StringComparison.Ordinal);
        Assert.Contains("\"resolution\":\"Resolution1080p\"", json, StringComparison.Ordinal);

        var restored = JsonSerializer.Deserialize<PluginConfiguration>(json, options);

        Assert.NotNull(restored);
        Assert.Equal(original.DefaultProfileId, restored!.DefaultProfileId);
        Assert.Equal(original.MaxConcurrentExports, restored.MaxConcurrentExports);
        Assert.Equal(original.NotifyOnFailure, restored.NotifyOnFailure);
        Assert.Equal(2, restored.Profiles.Count);
        Assert.Equal(original.Profiles[0].Format, restored.Profiles[0].Format);
        Assert.Equal(original.Profiles[0].Resolution, restored.Profiles[0].Resolution);
        Assert.Equal(original.Profiles[1].AudioChannels, restored.Profiles[1].AudioChannels);
        Assert.Equal(original.Profiles[1].OutputDirectory, restored.Profiles[1].OutputDirectory);
    }

    [Fact]
    public void JsonRoundTrip_ToleratesNumericEnumValues()
    {
        // Some dashboard builds echo enums as numbers; the page must not lose them.
        const string json = """
            {"profiles":[{"id":"6f1f6b64-3a4e-4c31-9a6f-6a1f8c0f0001","name":"x","format":1,"resolution":3,"audioChannels":2,"outputDirectory":"/tmp/x"}]}
            """;

        var config = JsonSerializer.Deserialize<PluginConfiguration>(
            json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(config);
        Assert.Single(config!.Profiles);
        Assert.Equal(ExportFormat.H265, config.Profiles[0].Format);
        Assert.Equal(ExportResolution.Resolution480p, config.Profiles[0].Resolution);
        Assert.Equal(ExportAudioChannels.Stereo, config.Profiles[0].AudioChannels);
    }

    [Fact]
    public void XmlRoundTrip_SurvivesUtf8SpecialCharacters()
    {
        var original = Sample();
        original.Profiles[0].Name = "Ünïcode — 1080p";
        var serializer = new XmlSerializer(typeof(PluginConfiguration));

        using var stream = new MemoryStream();
        serializer.Serialize(stream, original);
        stream.Position = 0;
        var restored = (PluginConfiguration?)serializer.Deserialize(stream);

        Assert.NotNull(restored);
        Assert.Equal("Ünïcode — 1080p", restored!.Profiles[0].Name);
    }
}
