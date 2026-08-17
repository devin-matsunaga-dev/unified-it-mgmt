using System.Security.Claims;

using Microsoft.Extensions.Logging.Abstractions;

using Platform.Dashboards;

namespace Infrastructure.Tests;

/// <summary>
/// The composer on its own, over hand-written widgets and no database (WP-5.5). What the real widgets
/// return is <see cref="DashboardApiIntegrationTests"/>'s job; what is here is what happens around them —
/// which widgets are asked, in what order, what the screen says when one of them falls over, and what the
/// views do to each other.
/// </summary>
public sealed class DashboardServiceTests
{
    private static readonly ClaimsPrincipal Technician = Principal("Technician");
    private static readonly ClaimsPrincipal Manager = Principal("Manager");
    private static readonly ClaimsPrincipal EndUser = Principal("EndUser");

    [Fact]
    public async Task Get_WithNoSavedView_DrawsTheRoleDefaultAndLoadsEveryVisibleWidget()
    {
        var widgets = AllWidgets();
        var service = Build(new FakeStore(), widgets);

        var response = await service.GetAsync(Manager, default);

        Assert.Equal(DashboardLayoutSource.RoleDefault, response.Layout.Source);
        Assert.Equal(DashboardPreset.Executive, response.Layout.Preset);
        Assert.Null(response.Layout.ViewId);
        Assert.Null(response.Layout.SavedAt);
        Assert.Empty(response.Views);
        Assert.Equal(
            DashboardDefaults.Executive.Select(placement => placement.Type),
            response.Layout.Placements.Select(placement => placement.Type));
        Assert.All(widgets, widget => Assert.Equal(1, widget.Calls));
        Assert.All(response.Widgets, widget => Assert.Equal(DashboardWidgetStatus.Loaded, widget.Status));
    }

    /// <summary>
    /// A widget this actor may not see is never loaded, and is reported as forbidden rather than as a
    /// widget that found nothing — WP-5.4's three-valued rule, restated. An empty licence-compliance card
    /// would be a claim about the estate; this is a fact about the account.
    /// </summary>
    [Fact]
    public async Task Get_AsAnActorWithoutTheRole_ReportsTheWidgetForbiddenAndNeverLoadsIt()
    {
        var widgets = AllWidgets();
        var service = Build(new FakeStore(), widgets);

        var response = await service.GetAsync(EndUser, default);

        Assert.All(widgets, widget => Assert.Equal(0, widget.Calls));
        Assert.All(response.Widgets, widget => Assert.Equal(DashboardWidgetStatus.NotPermitted, widget.Status));
        Assert.Empty(response.Layout.Placements);
        // The titles still travel, so anything reading this response knows what it was refused.
        Assert.All(response.Widgets, widget => Assert.False(string.IsNullOrWhiteSpace(widget.Title)));
    }

    /// <summary>
    /// The isolation that makes a five-module page survivable: one widget's query failing takes down its
    /// own card and nothing else. A licensing table nobody can reach must not hide what is broken on the
    /// network.
    /// </summary>
    [Fact]
    public async Task Get_WhenOneWidgetThrows_MarksThatCardFailedAndStillLoadsTheRest()
    {
        var broken = new FakeWidget(DashboardWidgetType.LicenseCompliance, throws: true);
        var service = Build(
            new FakeStore(),
            [new FakeWidget(DashboardWidgetType.SlaHealth), broken, new FakeWidget(DashboardWidgetType.NetworkStatus)]);

        var response = await service.GetAsync(Technician, default);

        var failed = Assert.Single(
            response.Widgets, widget => widget.Type == DashboardWidgetType.LicenseCompliance);
        Assert.Equal(DashboardWidgetStatus.Failed, failed.Status);
        // Not zero. A number that could not be read is not a number that is zero (WP-2.11), so the card
        // carries no segments to draw rather than a fabricated tally of nought.
        Assert.Empty(failed.Segments);
        Assert.Null(failed.Headline);
        Assert.Equal(2, response.Widgets.Count(widget => widget.Status == DashboardWidgetStatus.Loaded));
        // And it keeps its place: a failed widget is still where the reader left it.
        Assert.Contains(
            response.Layout.Placements, placement => placement.Type == DashboardWidgetType.LicenseCompliance);
    }

    [Fact]
    public async Task Get_WithAnActiveView_DrawsItInsteadOfTheDefaultAndSaysWhichOne()
    {
        var store = new FakeStore(
            View("Night shift", active: true, DashboardWidgetType.NetworkStatus, DashboardWidgetType.SlaHealth),
            View("Reporting"));
        var service = Build(store, AllWidgets());

        var response = await service.GetAsync(Manager, default);

        Assert.Equal(DashboardLayoutSource.Saved, response.Layout.Source);
        Assert.Equal("Night shift", response.Layout.Name);
        Assert.NotNull(response.Layout.ViewId);
        Assert.Equal(
            [DashboardWidgetType.NetworkStatus, DashboardWidgetType.SlaHealth],
            response.Layout.Placements.Select(placement => placement.Type));
        // Both views are listed, with the active one marked — that is the tab bar.
        Assert.Equal(["Night shift", "Reporting"], response.Views.Select(view => view.Name));
        Assert.Equal(["Night shift"], response.Views.Where(view => view.IsActive).Select(view => view.Name));
        // The preset is still reported: a saved view does not stop somebody being a manager, and the screen
        // has to be able to say which default they had before they saved anything.
        Assert.Equal(DashboardPreset.Executive, response.Layout.Preset);
    }

    /// <summary>
    /// The rule that replaced appending, and the one this feature would be worst without: a view holds
    /// exactly what its owner put in it. A blank view stays blank rather than refilling itself with every
    /// widget the platform has — which is what "create a new view" has to mean to be worth having.
    /// </summary>
    [Fact]
    public async Task Get_WithAnEmptyView_DrawsNothingRatherThanRefillingItself()
    {
        var service = Build(new FakeStore(View("Blank", active: true)), AllWidgets());

        var response = await service.GetAsync(Technician, default);

        Assert.Empty(response.Layout.Placements);
        Assert.Equal(DashboardLayoutSource.Saved, response.Layout.Source);
        // Every widget is still loaded and offered, so adding one back is a click and not a round trip.
        Assert.All(response.Widgets, widget => Assert.Equal(DashboardWidgetStatus.Loaded, widget.Status));
    }

    /// <summary>
    /// Widgets are loaded placed-first, so the cards actually on screen are the first queried — and the
    /// response lists every registered widget in enum order however the container handed them over.
    /// </summary>
    [Fact]
    public async Task Get_WhateverOrderTheWidgetsAreRegisteredIn_LoadsThePlacedOnesFirstAndListsThemInTypeOrder()
    {
        var order = new List<DashboardWidgetType>();
        var widgets = Enum.GetValues<DashboardWidgetType>()
            .Reverse()
            .Select(type => new FakeWidget(type, onLoad: order.Add))
            .ToArray();
        var store = new FakeStore(View("Two cards", active: true,
            DashboardWidgetType.RecentRootCauses, DashboardWidgetType.LicenseCompliance));
        var service = Build(store, widgets);

        var response = await service.GetAsync(Technician, default);

        Assert.Equal(
            [DashboardWidgetType.RecentRootCauses, DashboardWidgetType.LicenseCompliance],
            order.Take(2));
        Assert.Equal(Enum.GetValues<DashboardWidgetType>().Length, order.Count);
        Assert.Equal(
            Enum.GetValues<DashboardWidgetType>().OrderBy(type => (int)type),
            response.Widgets.Select(widget => widget.Type));
    }

    [Fact]
    public async Task CreateView_WithNoPlacements_IsABlankSlateAndBecomesTheOneOnScreen()
    {
        var store = new FakeStore();
        var service = Build(store, AllWidgets());

        var result = await service.CreateViewAsync(new SaveDashboardViewRequest("Blank", null), Technician, default);

        Assert.Equal(DashboardViewOutcome.Success, result.Outcome);
        Assert.Equal("dashboard-tests", store.OwnerId);
        Assert.Empty(result.Layout!.Placements);
        Assert.Equal("Blank", result.Layout.Name);
        Assert.Equal(DashboardLayoutSource.Saved, result.Layout.Source);
        Assert.Equal(["Blank"], result.Views!.Select(view => view.Name));
    }

    [Fact]
    public async Task CreateView_WithANameSomebodyAlreadyUsed_IsRefusedRatherThanCreatingASecondTab()
    {
        var service = Build(new FakeStore(View("Night shift", active: true)), AllWidgets());

        var result = await service.CreateViewAsync(
            new SaveDashboardViewRequest("night SHIFT", null), Technician, default);

        // Case-insensitively, because two tabs called "Night shift" and "night shift" are two tabs nobody
        // can tell apart.
        Assert.Equal(DashboardViewOutcome.NameInUse, result.Outcome);
        Assert.Null(result.Layout);
    }

    [Fact]
    public async Task CreateView_WhenTheOwnerAlreadyHasTheMaximum_IsRefused()
    {
        var store = new FakeStore(
            [.. Enumerable.Range(0, DashboardService.MaximumViews).Select(index => View($"View {index}"))]);
        var service = Build(store, AllWidgets());

        var result = await service.CreateViewAsync(new SaveDashboardViewRequest("One more", null), Technician, default);

        Assert.Equal(DashboardViewOutcome.TooMany, result.Outcome);
    }

    [Fact]
    public async Task SaveView_ReplacesItsCardsAndAnswersWithWhatWillBeDrawn()
    {
        var view = View("Night shift", active: true, DashboardWidgetType.SlaHealth);
        var store = new FakeStore(view);
        var service = Build(store, AllWidgets());

        var result = await service.SaveViewAsync(
            view.Id,
            new SaveDashboardViewRequest(null, [new(DashboardWidgetType.NetworkStatus, DashboardWidgetWidth.Full)]),
            Technician,
            default);

        Assert.Equal(DashboardViewOutcome.Success, result.Outcome);
        Assert.Equal(
            [DashboardWidgetType.NetworkStatus],
            result.Layout!.Placements.Select(placement => placement.Type));
        // The name is untouched when the save does not mention one — an arrangement is saved without having
        // to send the name back to keep it.
        Assert.Equal("Night shift", result.Layout.Name);
    }

    /// <summary>
    /// A view is stored whole, including a widget the saver cannot currently see, and narrowed only when it
    /// is drawn — so an arrangement survives somebody's roles changing and changing back.
    /// </summary>
    [Fact]
    public async Task SaveView_WithAWidgetTheActorCannotSee_StoresItAndDrawsWithoutIt()
    {
        var view = View("Mine", active: true);
        var store = new FakeStore(view);
        var service = Build(store, [new FakeWidget(DashboardWidgetType.SlaHealth)]);

        var result = await service.SaveViewAsync(
            view.Id,
            new SaveDashboardViewRequest(null,
            [
                new(DashboardWidgetType.SlaHealth, DashboardWidgetWidth.Half),
                new(DashboardWidgetType.LicenseCompliance, DashboardWidgetWidth.Half),
            ]),
            Technician,
            default);

        Assert.Equal(2, store.Saved!.Count);
        Assert.Equal(
            [DashboardWidgetType.SlaHealth],
            result.Layout!.Placements.Select(placement => placement.Type));
    }

    [Fact]
    public async Task SaveView_ThatDoesNotBelongToThisOwner_IsNotFound()
    {
        var service = Build(new FakeStore(), AllWidgets());

        var result = await service.SaveViewAsync(
            Guid.CreateVersion7(), new SaveDashboardViewRequest("Theirs", null), Technician, default);

        Assert.Equal(DashboardViewOutcome.NotFound, result.Outcome);
    }

    [Fact]
    public async Task SelectView_SwitchesWhichOneIsDrawn()
    {
        var night = View("Night shift", active: true, DashboardWidgetType.NetworkStatus);
        var reporting = View("Reporting", placements: DashboardWidgetType.LicenseCompliance);
        var service = Build(new FakeStore(night, reporting), AllWidgets());

        var result = await service.SelectViewAsync(reporting.Id, Technician, default);

        Assert.Equal(DashboardViewOutcome.Success, result.Outcome);
        Assert.Equal("Reporting", result.Layout!.Name);
        Assert.Equal(
            [DashboardWidgetType.LicenseCompliance],
            result.Layout.Placements.Select(placement => placement.Type));
    }

    [Fact]
    public async Task DeleteView_WhenItWasTheActiveOne_LeavesTheSurvivorOnScreen()
    {
        var night = View("Night shift", active: true);
        var reporting = View("Reporting", placements: DashboardWidgetType.LicenseCompliance);
        var service = Build(new FakeStore(night, reporting), AllWidgets());

        var result = await service.DeleteViewAsync(night.Id, Technician, default);

        Assert.Equal(DashboardViewOutcome.Success, result.Outcome);
        Assert.Equal("Reporting", result.Layout!.Name);
        Assert.Equal(["Reporting"], result.Views!.Select(view => view.Name));
    }

    [Fact]
    public async Task DeleteView_WhenItWasTheOnlyOne_PutsTheRoleDefaultBack()
    {
        var only = View("Mine", active: true, DashboardWidgetType.SlaHealth);
        var service = Build(new FakeStore(only), AllWidgets());

        var result = await service.DeleteViewAsync(only.Id, Manager, default);

        Assert.Equal(DashboardViewOutcome.Success, result.Outcome);
        Assert.Equal(DashboardLayoutSource.RoleDefault, result.Layout!.Source);
        Assert.Null(result.Layout.ViewId);
        Assert.Equal(
            DashboardDefaults.Executive.Select(placement => placement.Type),
            result.Layout.Placements.Select(placement => placement.Type));
        Assert.Empty(result.Views!);
    }

    /// <summary>
    /// The failure path on the writes: a token with no identity claim has nothing to own a view. It reads
    /// the role default happily — a read must not refuse what only a write has reason to refuse — but a save
    /// has nowhere to put the row.
    /// </summary>
    [Fact]
    public async Task CreateView_WithNoIdentityClaim_Throws()
    {
        var service = Build(new FakeStore(), AllWidgets());
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "Technician")], "Test"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateViewAsync(new SaveDashboardViewRequest("Mine", null), anonymous, default));

        var response = await service.GetAsync(anonymous, default);
        Assert.Equal(DashboardLayoutSource.RoleDefault, response.Layout.Source);
    }

    private static DashboardService Build(IDashboardViewStore store, params IDashboardWidget[] widgets) =>
        new(widgets, store, NullLogger<DashboardService>.Instance);

    private static FakeWidget[] AllWidgets() =>
        [.. Enum.GetValues<DashboardWidgetType>().Select(type => new FakeWidget(type))];

    private static StoredDashboardView View(
        string name,
        bool active = false,
        params DashboardWidgetType[] placements) =>
        new(
            Guid.CreateVersion7(),
            name,
            active,
            [.. placements.Select(type => new DashboardPlacement(type, DashboardWidgetWidth.Half))],
            DateTimeOffset.UnixEpoch);

    private static ClaimsPrincipal Principal(params string[] roles) =>
        new(new ClaimsIdentity(
            [.. roles.Select(role => new Claim(ClaimTypes.Role, role)), new Claim("sub", "dashboard-tests")],
            "Test"));

    private sealed class FakeWidget(
        DashboardWidgetType type,
        bool throws = false,
        Action<DashboardWidgetType>? onLoad = null) : IDashboardWidget
    {
        public DashboardWidgetType Type => type;

        public string Title => $"{type} widget";

        public int Calls { get; private set; }

        public bool IsVisibleTo(ClaimsPrincipal actor) => Platform.Actors.ActorRoles.IsAgent(actor);

        public Task<DashboardWidgetData> LoadAsync(DashboardWidgetQuery query, CancellationToken cancellationToken)
        {
            Calls++;
            onLoad?.Invoke(type);
            return throws
                ? throw new InvalidOperationException($"{type} is unreachable.")
                : Task.FromResult(new DashboardWidgetData(
                    "loaded", 1, "Things", [new DashboardSegment("All", 1, DashboardTone.Ok)], [], 0));
        }
    }

    /// <summary>
    /// The views in memory, behaving as the real store does: creating or selecting stands the others down,
    /// and deleting the active one promotes the most recently updated survivor.
    /// </summary>
    private sealed class FakeStore(params StoredDashboardView[] seed) : IDashboardViewStore
    {
        private readonly List<StoredDashboardView> _views = [.. seed];

        public string? OwnerId { get; private set; }

        public IReadOnlyList<DashboardPlacement>? Saved { get; private set; }

        public Task<IReadOnlyList<StoredDashboardView>> ListAsync(
            string ownerId, CancellationToken cancellationToken)
        {
            OwnerId = ownerId;
            return Task.FromResult<IReadOnlyList<StoredDashboardView>>([.. _views]);
        }

        public Task<StoredDashboardView> CreateAsync(
            string ownerId,
            string name,
            IReadOnlyList<DashboardPlacement> placements,
            CancellationToken cancellationToken)
        {
            OwnerId = ownerId;
            Saved = placements;
            Deactivate();
            var view = new StoredDashboardView(
                Guid.CreateVersion7(), name, true, placements, DateTimeOffset.UnixEpoch);
            _views.Add(view);
            return Task.FromResult(view);
        }

        public Task<StoredDashboardView?> UpdateAsync(
            string ownerId,
            Guid id,
            string? name,
            IReadOnlyList<DashboardPlacement>? placements,
            CancellationToken cancellationToken)
        {
            OwnerId = ownerId;
            var index = _views.FindIndex(view => view.Id == id);
            if (index < 0)
            {
                return Task.FromResult<StoredDashboardView?>(null);
            }

            if (placements is not null)
            {
                Saved = placements;
            }

            var updated = _views[index] with
            {
                Name = name ?? _views[index].Name,
                Placements = placements ?? _views[index].Placements,
            };
            _views[index] = updated;
            return Task.FromResult<StoredDashboardView?>(updated);
        }

        public Task<bool> DeleteAsync(string ownerId, Guid id, CancellationToken cancellationToken)
        {
            OwnerId = ownerId;
            var index = _views.FindIndex(view => view.Id == id);
            if (index < 0)
            {
                return Task.FromResult(false);
            }

            var wasActive = _views[index].IsActive;
            _views.RemoveAt(index);
            if (wasActive && _views.Count > 0)
            {
                _views[^1] = _views[^1] with { IsActive = true };
            }

            return Task.FromResult(true);
        }

        public Task<StoredDashboardView?> SelectAsync(string ownerId, Guid id, CancellationToken cancellationToken)
        {
            OwnerId = ownerId;
            var index = _views.FindIndex(view => view.Id == id);
            if (index < 0)
            {
                return Task.FromResult<StoredDashboardView?>(null);
            }

            Deactivate();
            _views[index] = _views[index] with { IsActive = true };
            return Task.FromResult<StoredDashboardView?>(_views[index]);
        }

        public Task<bool> NameExistsAsync(
            string ownerId, string name, Guid? excluding, CancellationToken cancellationToken) =>
            Task.FromResult(_views.Any(view =>
                view.Id != excluding && string.Equals(view.Name, name, StringComparison.OrdinalIgnoreCase)));

        private void Deactivate()
        {
            for (var index = 0; index < _views.Count; index++)
            {
                _views[index] = _views[index] with { IsActive = false };
            }
        }
    }
}
