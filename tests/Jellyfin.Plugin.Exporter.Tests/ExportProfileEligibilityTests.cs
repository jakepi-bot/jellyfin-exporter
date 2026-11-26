using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Exporter.Configuration;
using Jellyfin.Plugin.Exporter.Models;
using Xunit;

namespace Jellyfin.Plugin.Exporter.Tests;

public class ExportProfileEligibilityTests
{
    /// <summary>Probe stub so the rules can be tested without touching the file system.</summary>
    private sealed class StubInspector : IOutputDirectoryInspector
    {
        private readonly Func<string?, DirectoryProbeResult> _probe;

        public StubInspector(Func<string?, DirectoryProbeResult> probe) => _probe = probe;

        public static StubInspector AlwaysValid { get; } = new(_ => new DirectoryProbeResult(true, string.Empty, false));

        public DirectoryProbeResult Probe(string? path) => _probe(path);
    }

    private static PluginConfiguration Config(params ExportProfile[] profiles) => new()
    {
        Profiles = profiles.ToList(),
        MaxConcurrentExports = 1
    };

    private static ExportProfile Profile(string name, bool enabled = true, string directory = "/media/exports") => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        OutputDirectory = directory,
        Enabled = enabled
    };

    [Fact]
    public void EnabledProfileWithWritableDirectory_IsUsable()
    {
        var profile = Profile("1080p");
        var result = ExportProfileEligibility.Evaluate(Config(profile), profile.Id, StubInspector.AlwaysValid);

        Assert.True(result.IsUsable);
        Assert.Equal(profile.Id, result.Profile!.Id);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public void DisabledProfile_IsRejected()
    {
        var profile = Profile("archive", enabled: false);
        var result = ExportProfileEligibility.Evaluate(Config(profile), profile.Id, StubInspector.AlwaysValid);

        Assert.False(result.IsUsable);
        Assert.Contains("disabled", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("archive", result.ErrorMessage!);
    }

    [Fact]
    public void UnknownProfile_IsRejected()
    {
        var result = ExportProfileEligibility.Evaluate(Config(), Guid.NewGuid(), StubInspector.AlwaysValid);

        Assert.False(result.IsUsable);
        Assert.Contains("no longer exists", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnwritableOutputDirectory_IsRejected()
    {
        var profile = Profile("broken", directory: "relative/path");
        var inspector = new StubInspector(path => new DirectoryProbeResult(false, "The output directory must be absolute.", false));

        var result = ExportProfileEligibility.Evaluate(Config(profile), profile.Id, inspector);

        Assert.False(result.IsUsable);
        Assert.Contains("not usable", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("absolute", result.ErrorMessage!);
    }

    [Fact]
    public void EmptyGuid_IsRejectedWithoutLookingUpAnything()
    {
        var result = ExportProfileEligibility.Evaluate(Config(), Guid.Empty, StubInspector.AlwaysValid);

        Assert.False(result.IsUsable);
        Assert.Contains("Choose an export profile", result.ErrorMessage!);
    }
}
