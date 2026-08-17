using System.Security.Claims;

using Microsoft.Extensions.Logging;

using Platform.Actors;

namespace Platform.Dashboards;

/// <summary>
/// One dashboard over every module (WP-5.5). Asks each registered <see cref="IDashboardWidget"/> about its
/// own data and returns them in the order this person's active view puts them.
/// <para>
/// It holds no reference to any module: the widgets are injected, exactly as WP-5.4's five search sources
/// are. A host missing a module therefore draws the widgets it has, rather than failing to start or drawing
/// an empty card for data nothing could have loaded.
/// </para>
/// </summary>
public sealed class DashboardService(
    IEnumerable<IDashboardWidget> widgets,
    IDashboardViewStore views,
    ILogger<DashboardService> logger) : IDashboardService
{
    /// <summary>
    /// How many rows a widget names. Small on purpose: a card is a pointer at a list, not the list — the
    /// honest total travels beside the rows so "5 of 41" is what a reader sees, and the deep link is how
    /// they get the other 36.
    /// </summary>
    public const int RowLimit = 5;

    /// <summary>The most placements a view may hold. One per widget and no more; nothing repeats.</summary>
    public static int MaximumPlacements => Enum.GetValues<DashboardWidgetType>().Length;

    /// <summary>
    /// The most views one person may keep. Bounded because they are a jsonb row each and a tab bar stops
    /// being navigable long before this; high enough that nobody sensible meets it.
    /// </summary>
    public const int MaximumViews = 10;

    /// <summary>The longest a view's name may be. Long enough to be a sentence, short enough to be a tab.</summary>
    public const int MaximumNameLength = 60;

    public async Task<DashboardResponse> GetAsync(ClaimsPrincipal actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);

        // Throws if two widgets claim one member, which is a wiring mistake rather than a runtime state —
        // the same call SearchService makes about its sources, and it fails at the first request rather
        // than by silently drawing one of the two.
        var registered = widgets.ToDictionary(widget => widget.Type);
        var visible = Visible(actor);

        var stored = await ListAsync(actor, cancellationToken);
        var active = stored.FirstOrDefault(view => view.IsActive);
        var layout = Layout(actor, active, visible);

        // Every visible widget is loaded, not just the placed ones. Swapping a card through its title menu
        // and adding one then need no second round trip, and the cost is what this read has always paid:
        // before views existed, every widget was placed on every dashboard anyway.
        //
        // Sequentially, not fanned out, for WP-5.4's reason: these are a handful of indexed reads against
        // one database, and a page that opens five connections at once is a worse neighbour than one that
        // opens one. Placed widgets first, so the cards actually on screen are the first queried.
        var query = new DashboardWidgetQuery(actor, RowLimit);
        var order = layout.Placements.Select(placement => placement.Type)
            .Concat(visible.OrderBy(type => (int)type))
            .Distinct();

        var loaded = new Dictionary<DashboardWidgetType, DashboardWidgetResponse>(visible.Count);
        foreach (var type in order)
        {
            loaded[type] = await LoadAsync(registered[type], query, cancellationToken);
        }

        // Every registered widget appears, in enum order, including the ones this actor may not see. The
        // browser drops those rather than rendering them — but the response stays honest for anything else
        // that reads it, which is WP-5.4's rule about a group nobody is permitted rather than a group that
        // found nothing.
        var responses = registered.Values
            .OrderBy(widget => (int)widget.Type)
            .Select(widget => loaded.TryGetValue(widget.Type, out var response)
                ? response
                : Empty(widget, DashboardWidgetStatus.NotPermitted))
            .ToList();

        return new DashboardResponse(layout, Summarise(stored), responses);
    }

    public async Task<DashboardViewResult> CreateViewAsync(
        SaveDashboardViewRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(actor);

        var ownerId = OwnerId(actor);
        var name = (request.Name ?? string.Empty).Trim();

        if ((await views.ListAsync(ownerId, cancellationToken)).Count >= MaximumViews)
        {
            return new DashboardViewResult(DashboardViewOutcome.TooMany);
        }

        if (await views.NameExistsAsync(ownerId, name, null, cancellationToken))
        {
            return new DashboardViewResult(DashboardViewOutcome.NameInUse);
        }

        // An empty list rather than the role default: "new view" means a blank slate, and starting somebody
        // on a copy of the default would make the one thing they asked for the one thing they then have to
        // undo five times.
        await views.CreateAsync(ownerId, name, request.Placements ?? [], cancellationToken);
        return await StateAsync(actor, cancellationToken);
    }

    public async Task<DashboardViewResult> SaveViewAsync(
        Guid viewId,
        SaveDashboardViewRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(actor);

        var ownerId = OwnerId(actor);
        var name = request.Name?.Trim();

        if (name is not null && await views.NameExistsAsync(ownerId, name, viewId, cancellationToken))
        {
            return new DashboardViewResult(DashboardViewOutcome.NameInUse);
        }

        var saved = await views.UpdateAsync(ownerId, viewId, name, request.Placements, cancellationToken);
        return saved is null
            ? new DashboardViewResult(DashboardViewOutcome.NotFound)
            : await StateAsync(actor, cancellationToken);
    }

    public async Task<DashboardViewResult> SelectViewAsync(
        Guid viewId,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);

        var selected = await views.SelectAsync(OwnerId(actor), viewId, cancellationToken);
        return selected is null
            ? new DashboardViewResult(DashboardViewOutcome.NotFound)
            : await StateAsync(actor, cancellationToken);
    }

    public async Task<DashboardViewResult> DeleteViewAsync(
        Guid viewId,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);

        return await views.DeleteAsync(OwnerId(actor), viewId, cancellationToken)
            ? await StateAsync(actor, cancellationToken)
            : new DashboardViewResult(DashboardViewOutcome.NotFound);
    }

    /// <summary>
    /// What every write answers with: the views that now exist and the layout now on screen. Re-read rather
    /// than assembled from the request, so a caller sees what a subsequent <c>GET</c> would give it — a
    /// widget it cannot see is dropped here, and deleting the active view reports its successor.
    /// </summary>
    private async Task<DashboardViewResult> StateAsync(
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        var stored = await ListAsync(actor, cancellationToken);
        var active = stored.FirstOrDefault(view => view.IsActive);
        return new DashboardViewResult(
            DashboardViewOutcome.Success,
            Layout(actor, active, Visible(actor)),
            Summarise(stored));
    }

    private DashboardLayoutResponse Layout(
        ClaimsPrincipal actor,
        StoredDashboardView? active,
        IReadOnlyCollection<DashboardWidgetType> visible)
    {
        var preset = DashboardDefaults.PresetFor(actor);
        return new DashboardLayoutResponse(
            active is null ? DashboardLayoutSource.RoleDefault : DashboardLayoutSource.Saved,
            active?.Id,
            active?.Name,
            preset,
            active?.UpdatedAt,
            DashboardDefaults.Compose(active?.Placements, preset, visible));
    }

    private async Task<IReadOnlyList<StoredDashboardView>> ListAsync(
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        // A principal with no identity claim has nowhere to have saved a view, so it reads the role default
        // rather than failing: a read must not refuse a token that only a write has reason to refuse.
        var ownerId = ActorRoles.ActorId(actor);
        return ownerId is null ? [] : await views.ListAsync(ownerId, cancellationToken);
    }

    private static IReadOnlyList<DashboardViewSummary> Summarise(IReadOnlyList<StoredDashboardView> stored) =>
        [.. stored.Select(view => new DashboardViewSummary(view.Id, view.Name, view.IsActive, view.UpdatedAt))];

    /// <summary>
    /// One widget's data, or the fact that it could not be read.
    /// <para>
    /// A widget that throws takes down its own card and nothing else. The alternative — one failed query
    /// answering the whole page with a 500 — would mean a licensing table nobody can reach hides what is
    /// currently broken on the network, which is precisely backwards. The card then says so rather than
    /// showing zeroes: a number that could not be read is not zero (WP-2.11's rule, which this repo has
    /// now applied to KPI counts, status boards and drift reports).
    /// </para>
    /// </summary>
    private async Task<DashboardWidgetResponse> LoadAsync(
        IDashboardWidget widget,
        DashboardWidgetQuery query,
        CancellationToken cancellationToken)
    {
        try
        {
            var data = await widget.LoadAsync(query, cancellationToken);
            return new DashboardWidgetResponse(
                widget.Type,
                DashboardWidgetStatus.Loaded,
                widget.Title,
                data.Subtitle,
                data.Headline,
                data.HeadlineLabel,
                data.ToneOfHeadline,
                data.Segments,
                data.Rows,
                data.RowTotal,
                data.RowsTruncated,
                data.Link);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller went away. Not this widget's failure, and swallowing it would turn an abandoned
            // request into a card claiming the estate could not be read.
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Dashboard widget {Widget} failed to load.", widget.Type);
            return Empty(widget, DashboardWidgetStatus.Failed);
        }
    }

    private HashSet<DashboardWidgetType> Visible(ClaimsPrincipal actor) =>
        [.. widgets.Where(widget => widget.IsVisibleTo(actor)).Select(widget => widget.Type)];

    private static string OwnerId(ClaimsPrincipal actor) =>
        ActorRoles.ActorId(actor)
        ?? throw new InvalidOperationException("An authenticated actor identifier is required.");

    private static DashboardWidgetResponse Empty(IDashboardWidget widget, DashboardWidgetStatus status) =>
        new(widget.Type, status, widget.Title, null, null, null, DashboardTone.Neutral, [], [], 0, false, null);
}
