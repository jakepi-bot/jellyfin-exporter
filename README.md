# Jellyfin Exporter

A Jellyfin plugin that exports movies, shows and seasons to a directory you control, using
Jellyfin's own FFmpeg (hardware acceleration when the server has it) and named export profiles
that choose codec, resolution and audio channels.

**Status: 0.5.0.0.** The plugin installs, ships a working export-profile editor in the dashboard
settings, an **Exporter** page where an administrator browses the library and queues one export job
per item with a chosen profile, and an **Export Status** page with live progress, history and
failures with retry. A background worker drains the queue, runs Jellyfin's own FFmpeg one process
per job (hardware encoding when the server has it), writes the documented Movies/TV layout, reports
progress, captures FFmpeg errors, and leaves no partial files behind. The job history is persisted
across restarts, and every finished export raises a Jellyfin notification.

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

That endpoint is Jellyfin's own, and on a rejected save it answers `400` with a fixed
`Error processing request.` body; the reason is only in the server log. The settings page therefore
re-runs `Profiles/Validate` when a save fails and shows the validator's message (and the offending
profiles) in the page instead of the generic text. A direct API client still gets the generic body,
so `POST /JellyfinExporter/Profiles/Validate` is the way to read the reason programmatically.

## Browse and queue exports

The **Exporter** page (`/web/ConfigurationPage?name=Jellyfin%20Exporter`, also in the dashboard
menu — it is the plugin's only main-menu page, so the plugin card's Settings button and the
left-nav entry open the same page) has two tabs: **Export** and **Profiles**. The Export tab
browses in two modes: **Movies** (multi-select grid with per-item checkboxes and a
select-all-for-page control) and **TV Shows**, where clicking a show opens a season picker —
tick individual seasons or **All seasons**, optionally change the profile, then continue into the
normal confirmation. **Profiles** holds the profile editor and the global settings. Both modes page
through the real library with search and poster thumbnails. Pick a profile in the dropdown (only
enabled profiles are offered) and press **Export**. The page first asks the
server what the selection would queue, shows a confirmation summary (job count, already-queued
items, items that vanished), and only then enqueues.

Expansion happens on the server:

| Selection | Jobs |
| --- | --- |
| Movie, episode, video | one job for that item |
| Whole show | one job per episode across every season |
| Single season | one job per episode in that season |

Selecting a show and one of its seasons queues each episode once. Items already queued or running
with the same profile are skipped, so double-clicking Export does not duplicate work. Profiles that
are disabled or whose output directory fails validation are rejected with the reason.

Endpoints, all requiring an administrator:

| Route | Purpose |
| --- | --- |
| `GET /JellyfinExporter/Library/Profiles` | Profile ids and names for the dropdown (no paths) |
| `GET /JellyfinExporter/Library/Items?type=Movies|Series|Seasons&search=&startIndex=&limit=&parentId=` | Paged library browse; `parentId` narrows the result to one show's seasons |
| `POST /JellyfinExporter/Library/Plan` `{itemIds[], profileId}` | Dry run: what the selection would queue |
| `POST /JellyfinExporter/Library/Export` `{itemIds[], profileId}` | Expand and enqueue the jobs |

## Export engine

Every queued job is one media item exported with one profile. A background worker
(`Services/ExportQueueWorker`) drains `IExportQueue`, runs at most `MaxConcurrentExports`
processes (1-8, read from the live configuration on every poll), and records progress and the
terminal state on the job.

What each job does:

1. Resolve the Jellyfin item and its file on disk. A missing file or a virtual item fails the job
   with a readable message instead of starting FFmpeg.
2. `ffprobe` the file: dimensions, duration and whether it has audio. The real video stream is
   used, so embedded cover art never decides the output size.
3. Build the command from the job's profile snapshot (see the table below).
4. Encode into a hidden `.partial` file in the destination folder, parse
   `-progress pipe:1` into percentages, and rename it to the final name only when FFmpeg exits 0.
   A cancelled or failed export deletes the partial file, so a half-written file never appears
   under the finished name. The folders the job created are removed again while they are empty, so
   a failed or cancelled export leaves no artifact at all, not even an empty
   `Movies/{Title} ({Year})` or `TV/{Show}/Season {NN}` skeleton. A folder that already existed or
   that holds another job's file is never touched.

### FFmpeg, codecs and hardware acceleration

The binary always comes from Jellyfin's `IMediaEncoder.EncoderPath`/`ProbePath`; nothing is
bundled or hardcoded. Hardware acceleration is chosen with `IMediaEncoder.SupportsHwaccel` and
`IMediaEncoder.SupportsEncoder`, plus a real one-frame test encode: FFmpeg advertises hwaccels it
cannot use on the current machine (a container with no GPU still lists `cuda`), so the advertised
list alone would make every export fail. The family configured in Jellyfin's own encoding
settings is tried first, then CUDA, VA-API, QSV and VideoToolbox in that order; the first family
that passes the test encode wins, otherwise the exporter uses software encoding. Detection runs
once per server process and is logged (for example `cuda hardware encoding` or
`software encoding`), and `GET /JellyfinExporter/Engine` (admin only) reports the resolved path,
family and `libsvtav1` availability.

| Profile | Software encoder | Hardware encoder (nvenc / qsv / vaapi / videotoolbox) |
| --- | --- | --- |
| H.264 | `libx264 -preset medium -crf 20 -profile:v high` | `h264_nvenc` / `h264_qsv` / `h264_vaapi` / `h264_videotoolbox` |
| H.265 | `libx265 -preset medium -crf 24` | `hevc_nvenc` / `hevc_qsv` / `hevc_vaapi` / `hevc_videotoolbox` |
| AV1 | `libsvtav1 -preset 8 -crf 32` (or `libaom-av1` when SVT is absent) | `av1_nvenc` / `av1_qsv` / `av1_vaapi` (VideoToolbox has no AV1 encoder, so AV1 stays software there) |

Video is `-map 0:v:0` and scaled with `-vf scale=W:H` to fit the profile's box while preserving
aspect ratio, without upscaling (a 1080p source exported at 4K stays 1080p) and with even
dimensions. Audio is `-map 0:a?` plus:

| Audio channels | FFmpeg arguments |
| --- | --- |
| Original | `-c:a copy` |
| 5.1 | `-c:a aac -ac 6 -b:a 384k` |
| Stereo | `-c:a aac -ac 2 -b:a 192k` |

The container is always Matroska (`.mkv`). Matroska holds every codec these profiles produce and
every audio codec "Original" may copy (`DTS-HD`, `TrueHD`, `FLAC`, ...), so an export cannot fail
because the container rejects the source audio track.

Reference target bitrates, used for encoders that are bitrate-driven and as the documented
per-codec guidance: 4K 15M/10M/8M, 1080p 6M/4M/3M, 720p 3M/2M/1500k, 480p 1500k/1M/800k
(H.264/H.265/AV1).

### Cancellation and failures

`POST /Jobs/{id}/cancel` (or a server shutdown) cancels the job's token; the worker kills the
FFmpeg process tree, deletes the partial file and removes the empty destination folders the job
created, then marks the job `Cancelled`. A non-zero FFmpeg exit marks the job `Failed` with the
last 2000 characters of stderr as the error message and does the same folder cleanup.

The cleanup walks the job's own folder chain deepest-first (`Season NN`, then the show folder, then
`TV`, then the profile's output directory), deleting each folder that is empty and stopping at the
first one that is not: a season folder holding another episode (finished or still encoding) and
the profile's output directory when it already existed both survive. Nothing outside the job's own
output directory is ever considered.

## Status page, job history and notifications

**Dashboard > Plugins > Jellyfin Exporter > Export status** (also
`/web/index.html#/configurationpage?name=Export%20Status`) polls the queue every three seconds and
shows:

* active exports as cards with a live progress bar, percentage, elapsed time and a **Cancel**
  button (queued jobs cancel immediately, running jobs are stopped by killing FFmpeg);
* failed exports with the captured FFmpeg error and a **Retry** button that queues the same item,
  profile snapshot and output directory again;
* a history table of finished exports with the output path (or the error), the status and the
  duration, filterable by completed / failed / cancelled;
* a banner for each export that finished while the page was open, and the counts for the whole
  queue.

The page is admin-only, like the rest of the plugin API. Both pages are registered with the
plugin, so the settings page links to the status page and back.

### Job history survives restarts

Every status transition is written to `jobs.json` in the plugin's data folder
(`<config>/plugins/Jellyfin.Plugin.Exporter/jobs.json`), atomically (temporary file + rename), so the history
survives a server restart and a crash mid-write cannot truncate it. A file that cannot be parsed is
preserved as `jobs.corrupt-<timestamp>.json` instead of being deleted. The number of finished jobs
kept is the **Job history** setting (1-5000, default 200); queued and running jobs are never
pruned. On startup a job that was **queued** stays queued and the worker runs it again; a job that
was **running** when the server stopped cannot be resumed, so it is recorded as failed with
"the server restarted while this export was running" instead of showing as running forever.

### Notifications

Every terminal state raises a Jellyfin notification, once per job, and honours the two toggles in
the plugin settings (**Send a notification when an export completes / fails**):

```
Export completed: The Movie (Archive 1080p) -> /exports/Movies/The Movie (2020)/The Movie (2020).mkv
Export failed: The Movie (Archive 1080p) — FFmpeg exited with code 1: Unknown encoder 'h264_nvenc'
```

Jellyfin 12 removed the notification subsystem the 10.x plugins used: `INotificationManager`,
`NotificationRequest` and the `/Notifications` endpoints do not exist in the 12.4 plugin API, so a
plugin cannot post to the old dashboard notification drawer. The native equivalent in 12.4 is the
activity log, which the dashboard streams live on its Activity page, and that is what the plugin
writes: one entry per finished export (name `Export completed` / `Export failed`, the message above
as its overview, `Export completed`/`Export failed` as its type, severity Information/Error).
Delivery failures are logged and swallowed, so a broken activity database can never fail an export.

### API

| Endpoint | Purpose |
| --- | --- |
| `GET /JellyfinExporter/Jobs?status=&limit=` | Jobs plus counts, history limit, history file path and storage errors. `status` accepts `queued`, `running`, `completed`, `failed`, `cancelled`, `active` and `finished`, comma separated. |
| `POST /JellyfinExporter/Jobs/{id}/cancel` | Cancel a queued or running job (404 unknown, 409 already finished). |
| `POST /JellyfinExporter/Jobs/{id}/retry` | Queue a finished job again with the same snapshot (409 when the job is still active or an identical job is already queued). |

All three require an administrator. Job ids are accepted with or without dashes.

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
dotnet test -c Release               # unit tests + real-FFmpeg export tests
python3 scripts/package.py           # artifacts/jellyfin-exporter_<version>.zip
```

`dotnet test` covers the layout contract, command builder (every codec, resolution and audio
mode, plus CUDA/VA-API/QSV/VideoToolbox argument shapes), progress parsing, queue and worker
behavior, and real exports through the system FFmpeg: a movie, an episode, a whole season, all
four resolutions, all three audio modes, AV1, de-duplication, an induced FFmpeg failure and a
cancellation. Tests that need FFmpeg return early when `/usr/bin/ffmpeg` is absent. Set
`FFMPEG_PATH`/`FFPROBE_PATH` to test another build.

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
- `/web/ConfigurationPage?name=Jellyfin%20Exporter` serves the merged Exporter page (browse plus the
  profile editor), and only that page of the plugin is flagged for the main menu;
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

## Verify the status page and notifications

`scripts/verify-status.sh [port]` builds and packages the current checkout, serves the zip from a
local manifest, and drives a throwaway `jellyfin/jellyfin:12.4` container through the whole feature:

- seeds a finished job in `jobs.json` and proves it comes back after a restart (with a Guid in the
  undashed form Jellyfin writes);
- retries it, watches `GET /JellyfinExporter/Jobs` report progress above 0% while FFmpeg runs, and
  checks the completed job lands in the history with an output path that exists in the container;
- asserts the `Export completed: … -> path` activity entry;
- forces a real FFmpeg failure by exporting into a 2 MB tmpfs and asserts the error text and the
  `Export failed: … — FFmpeg exited with code 228 …` activity entry;
- proves a job that was running when the server stopped is recorded as failed;
- runs `scripts/verify-status-ui.js` in Chromium against the same container: the settings page shows
  the history limit, the status page renders the history, a retry shows a progress bar that
  advances, the completion banner appears, and the dashboard Activity page shows both notifications.

```bash
PORT=18110 bash scripts/verify-status.sh 18110     # RUN_UI=0 skips the browser checks
```

The browser checks need `playwright-core` and a cached Chromium; override the module location with
`NODE_PATH` and the binary with `CHROME`.

## Layout

```
Directory.Build.props                     version + pinned Jellyfin package versions
manifest.json                             plugin repository manifest (served to Jellyfin)
src/Jellyfin.Plugin.Exporter/
  Plugin.cs                               BasePlugin<PluginConfiguration> + IHasWebPages
  Api/ExporterProfilesController.cs       profile validation endpoints for the settings page
  Api/ExporterLibraryController.cs        browse/plan/enqueue endpoints for the Exporter page
  Api/ExporterJobsController.cs           job status/cancel/retry endpoints for the status page
  Configuration/PluginConfiguration.cs    persisted settings
  Configuration/PluginConfigurationValidator.cs  save-time rules (names, default, limits)
  Configuration/ExportProfileEligibility.cs      "may this profile queue work right now?"
  Configuration/OutputDirectoryInspector.cs      "is this directory writable?" probe
  Models/                                 shared domain models (see below)
  Export/                                 export engine: layout, FFmpeg builder, probe, runner
  Services/IExportQueue.cs                queue contract
  Services/ExportQueue.cs                 queue implementation drained by the worker
  Services/ExportQueueWorker.cs           background worker that runs FFmpeg
  Services/MediaTreeExpansion.cs          series/season -> episode expansion rules
  Services/ExportSelectionPlanner.cs      job building + already-queued guard
  Services/IExportJobStore.cs             durable job-history contract
  Services/JsonExportJobStore.cs          jobs.json in the plugin data folder (atomic writes)
  Services/ExportNotifications.cs         activity-log notifications + exactly-once listener
  Services/JellyfinMediaTreeSource.cs     ILibraryManager adapter for the browser
  Services/ExporterServiceRegistrator.cs  plugin DI registrations
  Web/exporterPage.html                   embedded Exporter page (browse + queue + profile editor)
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
| `ExportQueue` | in-memory implementation + worker-facing transitions (`TryBegin`, `ReportProgress`, `Complete`, `Fail`, `MarkCancelled`) |
| `ExportItemInfo` | `SourcePath`, `Title`, `Year`, `ShowName`, `SeasonNumber`, `EpisodeNumber`, `IsEpisode` |
| `IExportItemResolver` | Jellyfin item -> `ExportItemInfo` (`LibraryExportItemResolver`) |
| `IExportEngine` | `RunAsync(ExportJob, ExportItemInfo, Action<double>?, CancellationToken)` -> `ExportRunResult` |
| `IFfmpegLocator` | `Locate()` -> `FfmpegLocation` (`EncoderPath`, `ProbePath`, `HwAccel`, `SvtAv1Available`) |
| `OutputLayout` | layout contract + name sanitizing + de-duplication |
| `FfmpegCommandBuilder` | profile snapshot + probe -> argv list (codec, resolution, audio, hwaccel) |
| `MediaProbeInfo` | ffprobe facts: video codec, dimensions, duration, audio codec/channels |

Notes:

- Resolution members are prefixed because C# identifiers cannot begin with a digit. The UI shows
  `4K` / `1080p` / `720p` / `480p`; the FFmpeg layer maps them to `3840x2160`, `1920x1080`,
  `1280x720` and `854x480`.
- `ExportJob` carries a copy of its profile's format/resolution/audio/output directory so a job
  keeps its parameters if the profile is edited or deleted while it runs.
- Configuration is persisted by Jellyfin's XML serializer, so `PluginConfiguration.Profiles` is a
  concrete `List<ExportProfile>`. Enums travel as member names in both XML and JSON.

## Enqueue contract (for the worker and the status page)

- The browse page posts ids; the server expands them with `ILibraryManager`, so the queue only ever
  sees leaf items (`Movie`, `Episode`, `Video`, ...) with one job each.
- Each job snapshots the profile (`Format`, `Resolution`, `AudioChannels`, `OutputDirectory`) and is
  named for the operator (`Show S01E02 - Name` for episodes).
- The duplicate guard reads `IExportQueue.GetJobs()` and skips items that already have a
  non-terminal job for the same profile. It is best-effort: a worker that de-duplicates on enqueue
  closes the remaining race.
- `IExportQueue` is resolved from the request's service provider. When no implementation is
  registered, `POST /Export` answers 503 with an explanation rather than accepting jobs nobody runs.

## Output layout (contract for the export engine)

```
<OutputDirectory>/Movies/{Movie Title} ({Year})/{Movie Title} ({Year}).{ext}
<OutputDirectory>/TV/{Show Name}/Season {NN}/{NN} - {Episode Name}.{ext}
```

- `{ext}` is always `mkv`; the container is Matroska for every profile.
- The year is dropped when Jellyfin has none: `Movies/{Title}/{Title}.mkv`.
- Season and episode numbers are zero padded to two digits (`Season 02`, `03 - Pilot`).
- Characters that are unsafe in a file name (`< > : " / \ | ? *`) and control characters become
  `-`, whitespace collapses, leading/trailing dots and spaces are trimmed, names are capped at
  120 characters, and an empty title falls back to `Untitled`.
- A name that already exists is de-duplicated with a counter before the extension:
  `The Movie (2020) (1).mkv`. Concurrent jobs reserve their path before encoding, so two exports
  cannot write the same file.
- The file is written as `.{name}.{jobId}.partial` in its destination folder and renamed into
  place only after FFmpeg exits 0.

## License

GPL-3.0-only, matching Jellyfin's plugin ecosystem and the `Jellyfin.Controller` package license.
