using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.Exporter.Configuration;
using Jellyfin.Plugin.Exporter.Models;
using Xunit;

namespace Jellyfin.Plugin.Exporter.Tests;

public class ConfigurationValidationTests
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

    private static ExportProfile Profile(string name, string directory = "/media/exports", Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        Name = name,
        OutputDirectory = directory
    };

    [Fact]
    public void ValidConfiguration_HasNoIssues()
    {
        var config = Config(Profile("A"), Profile("B", "/media/other"));
        config.DefaultProfileId = config.Profiles[1].Id;

        Assert.Empty(PluginConfigurationValidator.Validate(config, StubInspector.AlwaysValid));
    }

    [Fact]
    public void EmptyConfiguration_IsAllowed()
    {
        // A fresh install has no profiles; the dashboard must still be able to save.
        Assert.Empty(PluginConfigurationValidator.Validate(new PluginConfiguration(), StubInspector.AlwaysValid));
    }

    [Fact]
    public void DuplicateNames_AreRejectedCaseInsensitively()
    {
        var config = Config(Profile("Archive"), Profile("archive"));

        var issues = PluginConfigurationValidator.Validate(config, StubInspector.AlwaysValid);

        var issue = Assert.Single(issues);
        Assert.Equal("Name", issue.Field);
        Assert.Equal(config.Profiles[1].Id, issue.ProfileId);
        Assert.Contains("unique", issue.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankName_IsRejected(string name)
    {
        var config = Config(Profile(name));

        var issues = PluginConfigurationValidator.Validate(config, StubInspector.AlwaysValid);

        var issue = Assert.Single(issues);
        Assert.Equal("Name", issue.Field);
        Assert.Contains("required", issue.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OverlongName_IsRejected()
    {
        var config = Config(Profile(new string('x', PluginConfigurationValidator.MaxProfileNameLength + 1)));

        var issue = Assert.Single(PluginConfigurationValidator.Validate(config, StubInspector.AlwaysValid));
        Assert.Equal("Name", issue.Field);
        Assert.Contains("limited", issue.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RelativeOutputDirectory_IsRejected()
    {
        var config = Config(Profile("A", "exports/movies"));

        var issue = Assert.Single(PluginConfigurationValidator.Validate(config, new OutputDirectoryInspector()));
        Assert.Equal("OutputDirectory", issue.Field);
        Assert.Contains("absolute", issue.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DirectoryProbeFailure_IsSurfacedPerProfile()
    {
        var config = Config(Profile("A"), Profile("B", "/nope"));
        var inspector = new StubInspector(path => path == "/nope"
            ? new DirectoryProbeResult(false, "The output directory is not writable: denied", false)
            : new DirectoryProbeResult(true, string.Empty, false));

        var issue = Assert.Single(PluginConfigurationValidator.Validate(config, inspector));
        Assert.Equal(config.Profiles[1].Id, issue.ProfileId);
        Assert.Equal("OutputDirectory", issue.Field);
        Assert.Contains("not writable", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DanglingDefaultProfile_IsRejected()
    {
        // This is what a delete of the default profile leaves behind if the page
        // forgets to reassign it.
        var config = Config(Profile("A"));
        config.DefaultProfileId = Guid.NewGuid();

        var issue = Assert.Single(PluginConfigurationValidator.Validate(config, StubInspector.AlwaysValid));
        Assert.Null(issue.ProfileId);
        Assert.Equal("DefaultProfileId", issue.Field);
        Assert.Contains("no longer exists", issue.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DuplicateProfileIds_AreRejected()
    {
        var id = Guid.NewGuid();
        var config = Config(Profile("A", id: id), Profile("B", "/media/other", id));

        var issue = Assert.Single(PluginConfigurationValidator.Validate(config, StubInspector.AlwaysValid));
        Assert.Equal("Id", issue.Field);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(PluginConfiguration.MaxConcurrentExportsLimit + 1)]
    public void MaxConcurrentExportsOutsideRange_IsRejected(int value)
    {
        var config = Config(Profile("A"));
        config.MaxConcurrentExports = value;

        var issue = Assert.Single(PluginConfigurationValidator.Validate(config, StubInspector.AlwaysValid));
        Assert.Equal("MaxConcurrentExports", issue.Field);
    }

    [Fact]
    public void GetDefaultProfile_FallsBackToFirstEnabledThenFirst()
    {
        var disabled = Profile("Disabled");
        disabled.Enabled = false;
        var enabled = Profile("Enabled", "/media/b");
        var config = Config(disabled, enabled);
        config.DefaultProfileId = Guid.NewGuid(); // dangling

        Assert.Equal(enabled.Id, config.GetDefaultProfile()?.Id);

        enabled.Enabled = false;
        Assert.Equal(disabled.Id, config.GetDefaultProfile()?.Id);
    }
}

public class OutputDirectoryInspectorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jf-exporter-probe-" + Guid.NewGuid().ToString("N"));

    public OutputDirectoryInspectorTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
            // A failed cleanup must not fail the test run.
        }
    }

    [Fact]
    public void ExistingWritableDirectory_IsValid()
    {
        var result = new OutputDirectoryInspector().Probe(_root);

        Assert.True(result.IsValid, result.Message);
        Assert.False(result.WillBeCreated);
    }

    [Fact]
    public void MissingDirectoryUnderWritableParent_IsCreatable()
    {
        var result = new OutputDirectoryInspector().Probe(Path.Combine(_root, "Movies", "Inception (2010)"));

        Assert.True(result.IsValid, result.Message);
        Assert.True(result.WillBeCreated);
        // Probing must not leave anything behind.
        Assert.False(Directory.Exists(Path.Combine(_root, "Movies")));
    }

    [Fact]
    public void RelativePath_IsRejected()
    {
        var result = new OutputDirectoryInspector().Probe("exports");

        Assert.False(result.IsValid);
        Assert.Contains("absolute", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EmptyPath_IsRejected()
    {
        Assert.False(new OutputDirectoryInspector().Probe("  ").IsValid);
        Assert.False(new OutputDirectoryInspector().Probe(null).IsValid);
    }

    [Fact]
    public void FilePath_IsRejected()
    {
        var file = Path.Combine(_root, "not-a-directory");
        File.WriteAllText(file, "x");

        var result = new OutputDirectoryInspector().Probe(file);

        Assert.False(result.IsValid);
        Assert.Contains("file", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadOnlyDirectory_IsRejected()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Permission bits do not behave the same way; covered on the Linux CI runner.
        }

        var readOnly = Path.Combine(_root, "readonly");
        Directory.CreateDirectory(readOnly);
        File.SetUnixFileMode(
            readOnly,
            UnixFileMode.UserRead | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);

        try
        {
            var result = new OutputDirectoryInspector().Probe(readOnly);

            Assert.False(result.IsValid);
            Assert.Contains("not writable", result.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.SetUnixFileMode(readOnly, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void ProbeLeavesNoFilesBehind()
    {
        new OutputDirectoryInspector().Probe(_root);

        Assert.Empty(Directory.GetFiles(_root, ".jellyfin-exporter-write-probe-*"));
    }
}
