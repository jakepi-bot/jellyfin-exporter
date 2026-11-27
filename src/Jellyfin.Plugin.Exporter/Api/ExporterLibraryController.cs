using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Exporter.Configuration;
using Jellyfin.Plugin.Exporter.Models;
using Jellyfin.Plugin.Exporter.Services;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Exporter.Api;

/// <summary>
/// Library browsing and export enqueueing for the Exporter page. Every route
/// requires an administrator: exports write files to the server.
/// </summary>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("JellyfinExporter/Library")]
public class ExporterLibraryController : ControllerBase
{
    private const int DefaultPageSize = 24;
    private const int MaxPageSize = 100;
    private const int SampleLabelCount = 5;

    /// <summary>
    /// Serializes the duplicate-guard read and the enqueue that follows it, so
    /// two Export requests in flight at once cannot both queue the same item.
    /// </summary>
    private static readonly object EnqueueGate = new();

    private readonly ILibraryManager _libraryManager;
    private readonly IOutputDirectoryInspector _inspector;
    private readonly ITranscodingSettingsSource? _transcoding;

    /// <summary>
    /// Initializes a new instance of the <see cref="ExporterLibraryController"/> class.
    /// </summary>
    /// <param name="libraryManager">Server library.</param>
    /// <param name="inspector">Output-directory probe used to reject unusable profiles.</param>
    /// <param name="transcoding">Jellyfin's transcoding settings, used to refuse formats Jellyfin disallows.</param>
    public ExporterLibraryController(
        ILibraryManager libraryManager,
        IOutputDirectoryInspector inspector,
        ITranscodingSettingsSource? transcoding = null)
    {
        _libraryManager = libraryManager;
        _inspector = inspector;
        _transcoding = transcoding;
    }

    private static IMediaTreeSource Tree(ILibraryManager libraryManager) => new JellyfinMediaTreeSource(libraryManager);

    /// <summary>
    /// Lists the profiles the page may offer. Ids and names only: output paths
    /// and encoder settings stay in the settings page.
    /// </summary>
    /// <returns>Configured profiles and which one is the default.</returns>
    [HttpGet("Profiles")]
    public ActionResult<ExporterProfileList> GetProfiles()
    {
        var configuration = Plugin.Instance?.Configuration;
        if (configuration is null)
        {
            return StatusCode(500, new ExporterApiError("The exporter configuration is not available yet."));
        }

        var defaultProfile = configuration.GetDefaultProfile();
        var profiles = (configuration.Profiles ?? new List<ExportProfile>())
            .Where(profile => profile is not null)
            .Select(profile => new ExporterProfileOption
            {
                Id = profile.Id,
                Name = profile.Name,
                Enabled = profile.Enabled,
                IsDefault = defaultProfile is not null && defaultProfile.Id == profile.Id
            })
            .ToArray();

        return new ExporterProfileList
        {
            DefaultProfileId = defaultProfile?.Id,
            Profiles = profiles
        };
    }

    /// <summary>
    /// Browses one library type with pagination and search.
    /// </summary>
    /// <param name="type">Movies, Series or Seasons.</param>
    /// <param name="search">Optional search term.</param>
    /// <param name="startIndex">Zero-based offset into the result set.</param>
    /// <param name="limit">Page size, 1-100.</param>
    /// <param name="parentId">
    /// Optional parent item id (dashed or undashed Guid) whose children should be
    /// listed, used by the season picker to load one show's seasons
    /// (<c>type=Seasons&amp;parentId=&lt;series id&gt;</c>). An unparseable value
    /// is ignored and the query behaves as if the parameter were absent, which is
    /// also what happens when it is not sent at all.
    /// </param>
    /// <returns>One page of library items.</returns>
    [HttpGet("Items")]
    public ActionResult<ExporterLibraryPage> GetItems(
        [FromQuery] string? type,
        [FromQuery] string? search,
        [FromQuery] int? startIndex,
        [FromQuery] int? limit,
        [FromQuery] string? parentId)
    {
        if (!TryMapLibraryType(type, out var kind, out var typeError))
        {
            return BadRequest(new ExporterApiError(typeError));
        }

        var start = Math.Max(0, startIndex ?? 0);
        var pageSize = Math.Clamp(limit ?? DefaultPageSize, 1, MaxPageSize);

        var query = new InternalItemsQuery
        {
            Recursive = true,
            IncludeItemTypes = new[] { kind },
            StartIndex = start,
            Limit = pageSize,
            EnableTotalRecordCount = true,
            OrderBy = new List<(ItemSortBy, SortOrder)>
            {
                (ItemSortBy.SortName, SortOrder.Ascending)
            }
        };

        var parent = TryParseGuid(parentId);
        if (parent is not null && parent.Value != Guid.Empty)
        {
            query.ParentId = parent.Value;
        }

        if (kind == BaseItemKind.Movie)
        {
            query.IsMissing = false;
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            query.SearchTerm = search.Trim();
        }

        var result = _libraryManager.GetItemsResult(query);
        var items = result.Items
            .Select(item => ToLibraryItem(item, kind))
            .ToArray();

        return new ExporterLibraryPage
        {
            Type = kind.ToString(),
            StartIndex = start,
            Limit = pageSize,
            TotalRecordCount = result.TotalRecordCount,
            Items = items
        };
    }

    /// <summary>
    /// Works out what a selection would queue, without queueing anything. The
    /// page uses this for its confirmation summary.
    /// </summary>
    /// <param name="payload">Selected item ids and the chosen profile.</param>
    /// <returns>Job counts and the items that would be skipped.</returns>
    [HttpPost("Plan")]
    public ActionResult<ExporterPlanResponse> Plan([FromBody] ExportRequestPayload? payload)
    {
        var preparation = Prepare(payload);
        if (preparation.Error is not null)
        {
            return StatusCode(preparation.Error.StatusCode, new ExporterApiError(preparation.Error.Message));
        }

        return BuildPlanResponse(preparation, queued: null);
    }

    /// <summary>
    /// Expands the selection and queues one job per item with the chosen profile.
    /// </summary>
    /// <param name="payload">Selected item ids and the chosen profile.</param>
    /// <returns>How many jobs were queued and what was skipped.</returns>
    [HttpPost("Export")]
    public ActionResult<ExporterPlanResponse> Export([FromBody] ExportRequestPayload? payload)
    {
        // The duplicate guard reads the queue and then writes to it. Two Export
        // requests arriving together (a double click, two tabs) would otherwise
        // both see an empty queue and enqueue the same item twice. Serializing
        // plan+enqueue closes that window; the queue itself is only entered for
        // a short in-memory append, so exports are not held up.
        lock (EnqueueGate)
        {
            var preparation = Prepare(payload);
            if (preparation.Error is not null)
            {
                return StatusCode(preparation.Error.StatusCode, new ExporterApiError(preparation.Error.Message));
            }

            var queue = HttpContext?.RequestServices?.GetService(typeof(IExportQueue)) as IExportQueue;
            if (queue is null)
            {
                return StatusCode(
                    503,
                    new ExporterApiError(
                        "The export queue is not running on this server yet, so nothing can be queued. " +
                        "Install the plugin build that includes the export worker."));
            }

            IReadOnlyList<ExportJob> queued;
            try
            {
                queued = queue.Enqueue(preparation.Plan!.Jobs);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new ExporterApiError("The export queue rejected the request: " + ex.Message));
            }

            return BuildPlanResponse(preparation, queued);
        }
    }

    private static bool TryMapLibraryType(string? type, out BaseItemKind kind, out string error)
    {
        error = string.Empty;
        switch ((type ?? "Movies").Trim().ToLowerInvariant())
        {
            case "movies":
            case "movie":
                kind = BaseItemKind.Movie;
                return true;
            case "series":
            case "shows":
            case "tvshows":
                kind = BaseItemKind.Series;
                return true;
            case "seasons":
            case "season":
                kind = BaseItemKind.Season;
                return true;
            default:
                kind = BaseItemKind.Movie;
                error = "Unknown library type. Use Movies, Series or Seasons.";
                return false;
        }
    }

    private ExporterLibraryItem ToLibraryItem(BaseItem item, BaseItemKind kind)
    {
        var node = JellyfinMediaTreeSource.ToNode(item);
        var poster = JellyfinMediaTreeSource.GetPoster(item);

        return new ExporterLibraryItem
        {
            Id = item.Id,
            Name = item.Name,
            ItemType = node.ItemType,
            Year = item.ProductionYear,
            SeriesName = node.SeriesName,
            SeasonNumber = node.SeasonNumber,
            EpisodeCount = kind switch
            {
                BaseItemKind.Series => CountEpisodes(item.Id, recursive: true),
                BaseItemKind.Season => CountEpisodes(item.Id, recursive: false),
                _ => null
            },
            HasImage = poster.ImageTag is not null,
            ImageItemId = poster.ImageItemId,
            ImageTag = poster.ImageTag
        };
    }

    private int? CountEpisodes(Guid parentId, bool recursive)
    {
        var count = _libraryManager.GetCount(new InternalItemsQuery
        {
            ParentId = parentId,
            Recursive = recursive,
            IncludeItemTypes = new[] { BaseItemKind.Episode },
            IsMissing = false
        });

        return count;
    }

    private ExporterPlanResponse BuildPlanResponse(Preparation preparation, IReadOnlyList<ExportJob>? queued)
    {
        var plan = preparation.Plan!;
        var sample = (queued ?? plan.Jobs)
            .Take(SampleLabelCount)
            .Select(job => job.ItemName)
            .ToArray();

        return new ExporterPlanResponse
        {
            ProfileId = preparation.Profile!.Id,
            ProfileName = preparation.Profile.Name,
            SelectedCount = preparation.SelectedCount,
            JobCount = plan.Jobs.Count,
            QueuedCount = queued?.Count,
            DuplicateCount = plan.Duplicates.Count,
            MissingCount = preparation.Expansion!.MissingItemIds.Count,
            ProblemCount = preparation.Expansion.Problems.Count,
            Problems = preparation.Expansion.Problems.Select(problem => problem.Message).ToArray(),
            SampleItems = sample
        };
    }

    private Preparation Prepare(ExportRequestPayload? payload)
    {
        if (payload is null)
        {
            return Preparation.Failed(400, "The request body is empty.");
        }

        var requestedIds = (payload.ItemIds ?? new List<string>())
            .Select(TryParseGuid)
            .Where(id => id.HasValue && id.Value != Guid.Empty)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();

        if (requestedIds.Count == 0)
        {
            return Preparation.Failed(400, "Select at least one movie, show or season.");
        }

        var configuration = Plugin.Instance?.Configuration;
        if (configuration is null)
        {
            return Preparation.Failed(500, "The exporter configuration is not available yet.");
        }

        var profileId = TryParseGuid(payload.ProfileId);
        if (profileId is null)
        {
            return Preparation.Failed(400, "Choose an export profile.");
        }

        var eligibility = ExportProfileEligibility.Evaluate(
            configuration,
            profileId.Value,
            _inspector,
            _transcoding?.Current);
        if (!eligibility.IsUsable)
        {
            return Preparation.Failed(400, eligibility.ErrorMessage!);
        }

        var profile = eligibility.Profile!;

        var source = Tree(_libraryManager);
        var expansion = MediaTreeExpansion.Expand(source, requestedIds);
        if (expansion.Items.Count == 0)
        {
            var messages = expansion.Problems.Select(problem => problem.Message).ToArray();
            var detail = messages.Length > 0
                ? string.Join(" ", messages)
                : "None of the selected items exist in the library any more.";
            return Preparation.Failed(400, detail);
        }

        IReadOnlyList<ExportJob> existingJobs;
        try
        {
            existingJobs = (HttpContext?.RequestServices?.GetService(typeof(IExportQueue)) as IExportQueue)?.GetJobs()
                ?? Array.Empty<ExportJob>();
        }
        catch (Exception)
        {
            // A queue that cannot report its jobs must not block new work; the
            // duplicate guard is best-effort protection, not a correctness gate.
            existingJobs = Array.Empty<ExportJob>();
        }

        var plan = ExportSelectionPlanner.Plan(profile, expansion.Items, existingJobs);

        return new Preparation
        {
            Profile = profile,
            Expansion = expansion,
            Plan = plan,
            SelectedCount = requestedIds.Count
        };
    }

    private static Guid? TryParseGuid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return Guid.TryParse(value, out var parsed) ? parsed : null;
    }

    private sealed class Preparation
    {
        public ExportProfile? Profile { get; init; }

        public MediaExpansion? Expansion { get; init; }

        public ExportPlan? Plan { get; init; }

        public int SelectedCount { get; init; }

        public PreparationError? Error { get; init; }

        public static Preparation Failed(int statusCode, string message) => new()
        {
            Error = new PreparationError(statusCode, message)
        };
    }

    private sealed record PreparationError(int StatusCode, string Message);
}

/// <summary>
/// Profiles offered in the export page's dropdown.
/// </summary>
public class ExporterProfileList
{
    /// <summary>Gets or sets the profile the page should preselect.</summary>
    public Guid? DefaultProfileId { get; set; }

    /// <summary>Gets or sets the configured profiles.</summary>
    public IReadOnlyList<ExporterProfileOption> Profiles { get; set; } = Array.Empty<ExporterProfileOption>();
}

/// <summary>
/// One profile in the export page's dropdown: id, name and whether it can queue work.
/// </summary>
public class ExporterProfileOption
{
    /// <summary>Gets or sets the profile id.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the operator-visible name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the profile may queue exports.</summary>
    public bool Enabled { get; set; }

    /// <summary>Gets or sets a value indicating whether this is the configured default.</summary>
    public bool IsDefault { get; set; }
}

/// <summary>
/// One page of library items.
/// </summary>
public class ExporterLibraryPage
{
    /// <summary>Gets or sets the library type that was asked for.</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Gets or sets the offset of the first item.</summary>
    public int StartIndex { get; set; }

    /// <summary>Gets or sets the page size.</summary>
    public int Limit { get; set; }

    /// <summary>Gets or sets the total number of matching items.</summary>
    public int TotalRecordCount { get; set; }

    /// <summary>Gets or sets the items on this page.</summary>
    public IReadOnlyList<ExporterLibraryItem> Items { get; set; } = Array.Empty<ExporterLibraryItem>();
}

/// <summary>
/// One item in the browse grid.
/// </summary>
public class ExporterLibraryItem
{
    /// <summary>Gets or sets the Jellyfin item id.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the item name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the Jellyfin item kind.</summary>
    public string ItemType { get; set; } = string.Empty;

    /// <summary>Gets or sets the production year, when known.</summary>
    public int? Year { get; set; }

    /// <summary>Gets or sets the series a season belongs to.</summary>
    public string? SeriesName { get; set; }

    /// <summary>Gets or sets the season number of a season row.</summary>
    public int? SeasonNumber { get; set; }

    /// <summary>Gets or sets how many episodes this show or season expands to.</summary>
    public int? EpisodeCount { get; set; }

    /// <summary>Gets or sets a value indicating whether a poster is available.</summary>
    public bool HasImage { get; set; }

    /// <summary>Gets or sets the item whose primary image should be shown (the series for an art-less season).</summary>
    public Guid ImageItemId { get; set; }

    /// <summary>Gets or sets the image cache tag.</summary>
    public string? ImageTag { get; set; }
}

/// <summary>
/// Selection posted by the export page.
/// </summary>
public class ExportRequestPayload
{
    /// <summary>Gets or sets the selected item ids, as strings so either Guid form is accepted.</summary>
    public IList<string>? ItemIds { get; set; }

    /// <summary>Gets or sets the chosen profile id.</summary>
    public string? ProfileId { get; set; }
}

/// <summary>
/// Result of planning or queueing a selection.
/// </summary>
public class ExporterPlanResponse
{
    /// <summary>Gets or sets the chosen profile id.</summary>
    public Guid ProfileId { get; set; }

    /// <summary>Gets or sets the chosen profile name.</summary>
    public string ProfileName { get; set; } = string.Empty;

    /// <summary>Gets or sets how many items the operator selected before expansion.</summary>
    public int SelectedCount { get; set; }

    /// <summary>Gets or sets how many jobs the selection expands to.</summary>
    public int JobCount { get; set; }

    /// <summary>Gets or sets how many jobs were queued; null for a dry run.</summary>
    public int? QueuedCount { get; set; }

    /// <summary>Gets or sets how many items were skipped because they are already queued for this profile.</summary>
    public int DuplicateCount { get; set; }

    /// <summary>Gets or sets how many selected ids Jellyfin no longer knows about.</summary>
    public int MissingCount { get; set; }

    /// <summary>Gets or sets how many selections could not be expanded.</summary>
    public int ProblemCount { get; set; }

    /// <summary>Gets or sets the explanations for selections that could not be expanded.</summary>
    public IReadOnlyList<string> Problems { get; set; } = Array.Empty<string>();

    /// <summary>Gets or sets a few job names so the confirmation shows what will be queued.</summary>
    public IReadOnlyList<string> SampleItems { get; set; } = Array.Empty<string>();
}

/// <summary>
/// Error returned by the library endpoints.
/// </summary>
public class ExporterApiError
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ExporterApiError"/> class.
    /// </summary>
    /// <param name="error">Operator-facing message.</param>
    public ExporterApiError(string error)
    {
        Error = error;
    }

    /// <summary>Gets or sets the operator-facing message.</summary>
    public string Error { get; set; }
}
