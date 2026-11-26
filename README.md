# Jellyfin Exporter

A Jellyfin plugin that exports movies, shows and seasons to a directory you control, using
Jellyfin's own FFmpeg (hardware acceleration when the server has it) and named export profiles
that choose codec, resolution and audio channels.

**Status: 0.2.0.0.** The plugin installs, ships a working export-profile editor in the dashboard
settings, validates profile names and output directories server-side, and persists profiles
across restarts. The export engine, browse UI and status dashboard land in follow-up changes on
top of this skeleton.

## Export profiles

Open **Dashboard > Plugins > Jellyfin Exporter** to manage profiles. A profile is a named
preset:

| Field | Values |
| --- | --- |
| Name | Free text, unique, 1-64 characters |
| Format | H.264 / AVC, H.265 / HEVC, AV1 |
| Resolution | 4K (3840x2160), 1080p, 720p, 480p |
| Audio channels | Original (copy), 5.1 (AAC), Stereo (AAC) |
| Output directory | Absolute path on the server, e.g. `/mnt/media/exports` |
| Enabled | Disabled profiles stay configured but cannot queue new exports |

One profile is marked **default** (the radio in the Default column). The default cannot be
deleted until another profile is made default. Global settings cover the maximum number of
concurrent exports (1-8) and the completion/failure notifications.

Validation happens twice: the page checks names and path shape inline, then asks the server to
probe the directory (`POST /JellyfinExporter/Profiles/Validate` and
`POST /JellyfinExporter/Profiles/ProbeDirectory`). A profile is rejected unless its output
directory is absolute, is a directory (or creatable) and accepts a probe file, because the export
would otherwise fail after encoding. `POST /Plugins/{id}/Configuration` runs the same checks
again, so the API cannot store a profile the exporter could not use.

## Requirements

| Component | Version |
| --- | --- |
| Jellyfin server | 12.4 (`targetAbi` 12.4.0.0) |
| .NET SDK (build only) | 10.0 |
| Jellyfin.Controller / Jellyfin.Model | 12.4.0 |

The plugin targets `net10.0` because the Jellyfin 12.4 NuGet packages ship `net10.0` assemblies
only (10.11.x packages are `net9.0`). Pinning anything older would not compile against a stock
12.4 server.

## Install

Add this URL under **Dashboard > Plugins > Repositories**:

```
https://raw.githubusercontent.com/jakepi-bot/jellyfin-exporter/main/manifest.json
```

Then open the **General** catalogue, install **Jellyfin Exporter** and restart Jellyfin. The
plugin appears in the plugin list with a settings page.

For a manual install, download the zip from the latest GitHub release and extract it into a
folder under the server's `plugins` directory (default `config/plugins/Jellyfin Exporter`), then
restart. The zip contains `Jellyfin.Plugin.Exporter.dll`, `meta.json` and `plugin.xml` at its root.

## Build and package

```bash
export PATH="$HOME/.dotnet:$PATH"    # .NET 10 SDK
dotnet build -c Release
dotnet test -c Release               # configuration round-trip + validation rules
python3 scripts/package.py           # artifacts/jellyfin-exporter_<version>.zip
```

`scripts/package.py` reads `<Version>` and `<JellyfinTargetAbi>` from `Directory.Build.props`,
publishes the project, and stamps both values into the `meta.json` and `plugin.xml` inside the
zip. The repository copies of those files are not rewritten.

## Release

1. Bump `<Version>` in `Directory.Build.props` (four-part version, e.g. `0.2.0.0`).
2. Commit and tag: `git tag v0.2.0.0 && git push origin main --tags`.
3. The **Release** workflow builds, zips, creates the GitHub release with the zip attached, then
   adds the version and its MD5 checksum to `manifest.json` and commits it to `main`.

`manifest.json` is what Jellyfin fetches from the repository URL; the release workflow is the only
thing that should write version entries into it. To verify a published artifact against the
manifest:

```bash
python3 scripts/update_manifest.py --version 0.1.0.1 --zip artifacts/jellyfin-exporter_0.1.0.1.zip --check
```

## Verify a published repository

`scripts/verify-install.sh` boots a throwaway `jellyfin/jellyfin:12.4` container, completes the
startup wizard, registers a plugin repository URL, installs the plugin from the catalogue,
restarts the server and asserts that:

- the plugin loads as `Active` at the expected version and can be uninstalled;
- `/web/ConfigurationPage?name=Jellyfin%20Exporter` serves the embedded settings page;
- the configuration API returns defaults and persists a change across a restart.

```bash
bash scripts/verify-install.sh \
  https://raw.githubusercontent.com/jakepi-bot/jellyfin-exporter/main/manifest.json 18096
```

Requires Docker; the script uses `sudo -n` to clean the previous run's root-owned volumes.
`PLUGIN_VERSION`, `IMAGE`, `NAME`, `PORT` and `WORK` can be overridden from the environment.

Two operational notes:

- `raw.githubusercontent.com` caches for a few minutes, so a freshly committed `manifest.json`
  can take up to ~5 minutes to appear. Trigger **Update Plugins** again after that.
- Jellyfin downloads `versions[].sourceUrl` directly and rejects anything that is not a `.zip`;
  `scripts/update_manifest.py` points it at the GitHub release asset, not the repository page.

## Layout

```
Directory.Build.props                     version + pinned Jellyfin package versions
manifest.json                             plugin repository manifest (served to Jellyfin)
src/Jellyfin.Plugin.Exporter/
  Plugin.cs                               BasePlugin<PluginConfiguration> + IHasWebPages
  Api/ExporterProfilesController.cs       profile validation endpoints for the settings page
  Configuration/PluginConfiguration.cs    persisted settings
  Configuration/PluginConfigurationValidator.cs  save-time rules (names, default, limits)
  Configuration/OutputDirectoryInspector.cs      "is this directory writable?" probe
  Models/                                 shared domain models (see below)
  Services/                               IExportQueue contract + DI registrator
  Web/configPage.html                     embedded settings page (profile editor)
  meta.json, plugin.xml                   install metadata stamped into the zip
tests/Jellyfin.Plugin.Exporter.Tests/     xunit tests: XML/JSON round-trip + validation rules
scripts/package.py                        build the installable zip
scripts/update_manifest.py                maintain manifest.json
scripts/verify-install.sh                 install + assert against a stock Jellyfin container
.github/workflows/build.yml               build + test + package on every push
.github/workflows/release.yml             publish zip + manifest on tag
```

## Domain contract

Everything below lives in `Jellyfin.Plugin.Exporter.Models` / `.Services` so sibling changes
compile against one definition.

| Type | Members |
| --- | --- |
| `ExportProfile` | `Id`, `Name`, `Format`, `Resolution`, `AudioChannels`, `OutputDirectory`, `Enabled` |
| `ExportFormat` | `H264`, `H265`, `AV1` |
| `ExportResolution` | `Resolution4K`, `Resolution1080p`, `Resolution720p`, `Resolution480p` |
| `ExportAudioChannels` | `Original`, `Surround51`, `Stereo` |
| `ExportJob` | `Id`, `JellyfinItemId`, `ItemName`, `ItemType`, `ProfileId`, `ProfileName`, `Status`, `ProgressPercent`, `OutputPath`, `ErrorMessage`, `CreatedAt`, `StartedAt`, `FinishedAt`, plus a snapshot of `Format`, `Resolution`, `AudioChannels`, `OutputDirectory` |
| `ExportJobStatus` | `Queued`, `Running`, `Completed`, `Failed`, `Cancelled` |
| `IExportQueue` | `event JobStatusChanged`, `Enqueue(IEnumerable<ExportJob>)`, `GetJobs()`, `Cancel(Guid)` |

Notes:

- Resolution members are prefixed because C# identifiers cannot begin with a digit. The UI shows
  `4K` / `1080p` / `720p` / `480p`; the FFmpeg layer maps them to `3840x2160`, `1920x1080`,
  `1280x720` and `854x480`.
- `ExportJob` carries a copy of its profile's format/resolution/audio/output directory so a job
  keeps its parameters if the profile is edited or deleted while it runs.
- Configuration is persisted by Jellyfin's XML serializer, so `PluginConfiguration.Profiles` is a
  concrete `List<ExportProfile>`. Enums travel as member names in both XML and JSON.

## Output layout (contract for the export engine)

```
<OutputDirectory>/Movies/{Movie Title} ({Year})/{Movie Title} ({Year}).{ext}
<OutputDirectory>/TV/{Show Name}/Season {NN}/{NN} - {Episode Name}.{ext}
```

## License

GPL-3.0-only, matching Jellyfin's plugin ecosystem and the `Jellyfin.Controller` package license.
