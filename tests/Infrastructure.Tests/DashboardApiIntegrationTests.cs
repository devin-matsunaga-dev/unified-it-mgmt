using System.Net;
using System.Net.Http.Json;

using Microsoft.Extensions.DependencyInjection;

using Modules.Helpdesk.Data;
using Modules.Monitoring.Data;

using Platform.Dashboards;

namespace Infrastructure.Tests;

/// <summary>
/// The unified dashboard end to end (WP-5.5): one call to <c>GET /api/dashboard</c> reaching three modules'
/// schemas and coming back as one laid-out screen, plus the two writes that make an arrangement stick.
/// <para>
/// The estate is shared by the whole suite, so nothing here asserts an absolute count — every number on this
/// screen is estate-wide by definition. What is asserted instead is the structure, the deltas this class
/// causes itself, and the layouts, which belong to one subject and are therefore the only part of the
/// feature that can be isolated.
/// </para>
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class DashboardApiIntegrationTests(InfrastructureFixture infrastructure, DashboardHostFixture host)
    : IClassFixture<DashboardHostFixture>, IAsyncLifetime
{
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        ArgumentNullException.ThrowIfNull(host);
        await host.EnsureInitialisedAsync(infrastructure);
        _client = host.Client;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// The whole point of the feature: one read, three modules, one screen — and every widget the platform
    /// registers comes back having actually run.
    /// </summary>
    [Fact]
    public async Task Get_AsAnOperator_ReturnsEveryWidgetLoadedAndPlaced()
    {
        var response = await GetAsync();

        Assert.Equal(
            Enum.GetValues<DashboardWidgetType>().Select(type => type.ToString()),
            response.Widgets.Select(widget => widget.Type));
        Assert.All(response.Widgets, widget => Assert.Equal("Loaded", widget.Status));
        Assert.All(response.Widgets, widget => Assert.False(string.IsNullOrWhiteSpace(widget.Title)));
        Assert.Equal(
            Enum.GetValues<DashboardWidgetType>().Length,
            response.Layout.Placements.Count);
    }

    /// <summary>
    /// The registration rule, asserted against the real host: every member of the vocabulary has exactly one
    /// widget behind it. WP-5.4 put the same guard on its search sources, and the failure here is worse — a
    /// member with no widget would leave a hole in every saved layout that nothing could fill.
    /// </summary>
    [Fact]
    public void EveryWidgetType_HasExactlyOneRegisteredWidget()
    {
        using var scope = host.Services.CreateScope();
        var widgets = scope.ServiceProvider.GetServices<IDashboardWidget>().ToList();

        Assert.Equal(
            Enum.GetValues<DashboardWidgetType>().OrderBy(type => (int)type),
            widgets.Select(widget => widget.Type).OrderBy(type => (int)type));
    }

    /// <summary>WP-5.5's first verification step: a manager opens on the executive default.</summary>
    [Fact]
    public async Task Get_AsAManagerWhoHasSavedNothing_IsTheExecutiveDefault()
    {
        var response = await GetAsync(role: "Manager", subject: Subject());

        Assert.Equal("RoleDefault", response.Layout.Source);
        Assert.Equal("Executive", response.Layout.Preset);
        Assert.Null(response.Layout.SavedAt);
        Assert.Equal(
            DashboardDefaults.Executive.Select(placement => placement.Type.ToString()),
            response.Layout.Placements.Select(placement => placement.Type));
        // The default is not all cards any more: the one widget that is purely a split of a whole opens as
        // a donut, which is also how somebody discovers the shapes exist.
        Assert.Equal(
            DashboardDefaults.Executive.Select(placement => placement.Display.ToString()),
            response.Layout.Placements.Select(placement => placement.Display));
        Assert.Contains("Donut", response.Layout.Placements.Select(placement => placement.Display));
    }

    [Fact]
    public async Task Get_AsATechnicianWhoHasSavedNothing_IsTheOperationsDefault()
    {
        var response = await GetAsync(role: "Technician", subject: Subject());

        Assert.Equal("RoleDefault", response.Layout.Source);
        Assert.Equal("Operations", response.Layout.Preset);
        Assert.Equal(
            DashboardDefaults.Operations.Select(placement => placement.Type.ToString()),
            response.Layout.Placements.Select(placement => placement.Type));
    }

    /// <summary>
    /// WP-5.5's second verification step, end to end: arrange, save, read it back. Saved against a subject
    /// of this test's own, because a view belongs to a person.
    /// </summary>
    [Fact]
    public async Task CreateView_ThenGet_ReturnsTheArrangementThatWasSaved()
    {
        var subject = Subject();
        var placements = new[]
        {
            new { type = "LicenseCompliance", width = "Full" },
            new { type = "RecentRootCauses", width = "Half" },
            new { type = "SlaHealth", width = "Half" },
        };

        var saved = await CreateViewAsync("Night shift", placements, subject: subject);
        Assert.Equal("Saved", saved.Layout.Source);
        Assert.Equal("Night shift", saved.Layout.Name);
        Assert.Equal(
            placements.Select(placement => placement.type),
            saved.Layout.Placements.Select(placement => placement.Type));

        var response = await GetAsync(subject: subject);
        Assert.Equal("Saved", response.Layout.Source);
        Assert.NotNull(response.Layout.SavedAt);
        Assert.Equal(
            placements.Select(placement => placement.type),
            response.Layout.Placements.Select(placement => placement.Type));
        Assert.Equal("Full", response.Layout.Placements[0].Width);
        Assert.Equal(["Night shift"], response.Views.Select(view => view.Name));
    }

    /// <summary>
    /// The blank slate the WP-5.5 rework asked for: a new view starts with nothing on it and <b>stays</b>
    /// empty. The first cut appended every unplaced widget, which would have refilled this the moment it
    /// was read.
    /// </summary>
    [Fact]
    public async Task CreateView_WithNoPlacements_IsBlankAndStaysBlank()
    {
        var subject = Subject();

        var created = await CreateViewAsync("Blank slate", [], subject: subject);
        Assert.Empty(created.Layout.Placements);

        var response = await GetAsync(subject: subject);
        Assert.Empty(response.Layout.Placements);
        Assert.Equal("Blank slate", response.Layout.Name);
        // Every widget is still loaded and offered, so adding one is a click rather than a round trip.
        Assert.All(response.Widgets, widget => Assert.Equal("Loaded", widget.Status));
    }

    /// <summary>Several views at once, with exactly one of them active — that is the tab bar.</summary>
    [Fact]
    public async Task CreateView_Twice_KeepsBothAndLeavesTheNewestOnScreen()
    {
        var subject = Subject();
        await CreateViewAsync("Morning", [new { type = "SlaHealth", width = "Full" }], subject: subject);
        await CreateViewAsync("Evening", [new { type = "NetworkStatus", width = "Full" }], subject: subject);

        var response = await GetAsync(subject: subject);

        Assert.Equal(["Morning", "Evening"], response.Views.Select(view => view.Name));
        Assert.Equal(["Evening"], response.Views.Where(view => view.IsActive).Select(view => view.Name));
        Assert.Equal("NetworkStatus", response.Layout.Placements[0].Type);
    }

    [Fact]
    public async Task SelectView_SwitchesWhichViewIsDrawn()
    {
        var subject = Subject();
        var morning = await CreateViewAsync("Morning", [new { type = "SlaHealth", width = "Full" }], subject: subject);
        await CreateViewAsync("Evening", [new { type = "NetworkStatus", width = "Full" }], subject: subject);

        using var request = DashboardHostFixture.Authenticate(
            new HttpRequestMessage(
                HttpMethod.Post, $"/api/dashboard/views/{morning.Layout.ViewId}/selection"),
            subject: subject);
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var after = await GetAsync(subject: subject);
        Assert.Equal("Morning", after.Layout.Name);
        Assert.Equal("SlaHealth", after.Layout.Placements[0].Type);
    }

    [Fact]
    public async Task SaveView_ReplacesItsCardsWithoutTouchingItsName()
    {
        var subject = Subject();
        var created = await CreateViewAsync("Mine", [new { type = "SlaHealth", width = "Full" }], subject: subject);

        var saved = await SaveViewAsync(
            created.Layout.ViewId!.Value,
            new { placements = new[] { new { type = "NetworkStatus", width = "Third" } } },
            subject: subject);

        Assert.Equal("Mine", saved.Layout.Name);
        Assert.Equal(["NetworkStatus"], saved.Layout.Placements.Select(placement => placement.Type));
        Assert.Equal("Third", saved.Layout.Placements[0].Width);
    }

    [Fact]
    public async Task SaveView_WithOnlyAName_RenamesItAndLeavesItsCardsAlone()
    {
        var subject = Subject();
        var created = await CreateViewAsync("Before", [new { type = "SlaHealth", width = "Full" }], subject: subject);

        var saved = await SaveViewAsync(created.Layout.ViewId!.Value, new { name = "After" }, subject: subject);

        Assert.Equal("After", saved.Layout.Name);
        Assert.Equal(["SlaHealth"], saved.Layout.Placements.Select(placement => placement.Type));
    }

    [Fact]
    public async Task DeleteView_WhenItWasTheActiveOne_LeavesTheSurvivorOnScreen()
    {
        var subject = Subject();
        await CreateViewAsync("Morning", [new { type = "SlaHealth", width = "Full" }], subject: subject);
        var evening = await CreateViewAsync(
            "Evening", [new { type = "NetworkStatus", width = "Full" }], subject: subject);

        using var request = DashboardHostFixture.Authenticate(
            new HttpRequestMessage(HttpMethod.Delete, $"/api/dashboard/views/{evening.Layout.ViewId}"),
            subject: subject);
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var after = await GetAsync(subject: subject);
        Assert.Equal("Morning", after.Layout.Name);
        Assert.Equal(["Morning"], after.Views.Select(view => view.Name));
    }

    [Fact]
    public async Task DeleteView_WhenItWasTheOnlyOne_PutsTheRoleDefaultBack()
    {
        var subject = Subject();
        var only = await CreateViewAsync(
            "Mine", [new { type = "LicenseCompliance", width = "Full" }], role: "Manager", subject: subject);

        using var request = DashboardHostFixture.Authenticate(
            new HttpRequestMessage(HttpMethod.Delete, $"/api/dashboard/views/{only.Layout.ViewId}"),
            "Manager", subject);
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var after = await GetAsync(role: "Manager", subject: subject);
        Assert.Equal("RoleDefault", after.Layout.Source);
        Assert.Empty(after.Views);
        Assert.Equal(
            DashboardDefaults.Executive.Select(placement => placement.Type.ToString()),
            after.Layout.Placements.Select(placement => placement.Type));
    }

    /// <summary>Somebody else's view is a 404, and deliberately indistinguishable from one that never existed.</summary>
    [Fact]
    public async Task SaveView_ThatDoesNotBelongToTheCaller_Is404()
    {
        var owner = Subject();
        var created = await CreateViewAsync("Mine", [new { type = "SlaHealth", width = "Full" }], subject: owner);

        using var request = DashboardHostFixture.Authenticate(
            new HttpRequestMessage(HttpMethod.Put, $"/api/dashboard/views/{created.Layout.ViewId}"),
            subject: Subject());
        request.Content = JsonContent.Create(new { name = "Theirs now" });
        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// Two tabs with one name is a tab bar nobody can navigate, so the second is refused — case-insensitively,
    /// because "Night shift" and "night shift" are the same tab to a reader.
    /// </summary>
    [Fact]
    public async Task CreateView_WithANameAlreadyTaken_Is409()
    {
        var subject = Subject();
        await CreateViewAsync("Night shift", [], subject: subject);

        using var request = DashboardHostFixture.Authenticate(
            new HttpRequestMessage(HttpMethod.Post, "/api/dashboard/views"), subject: subject);
        request.Content = JsonContent.Create(new { name = "NIGHT SHIFT", placements = Array.Empty<object>() });
        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task CreateView_WithNoName_Is400()
    {
        using var request = DashboardHostFixture.Authenticate(
            new HttpRequestMessage(HttpMethod.Post, "/api/dashboard/views"), subject: Subject());
        request.Content = JsonContent.Create(new { name = "   ", placements = Array.Empty<object>() });

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDto>();
        Assert.Contains("name", problem!.Errors!.Keys);
    }

    /// <summary>
    /// A card can be kept as a chart, and the shape survives a round trip. It is a presentation choice, so
    /// it rides with the placement rather than with the widget's data — the server stores it and validates
    /// it and has no other opinion about it.
    /// </summary>
    [Fact]
    public async Task CreateView_WithAChartShape_KeepsIt()
    {
        var subject = Subject();

        var created = await CreateViewAsync(
            "Charted",
            [
                new { type = "OpenByPriority", width = "Third", display = "Donut" },
                new { type = "NetworkStatus", width = "Third", display = "Bar" },
            ],
            subject: subject);

        Assert.Equal(["Donut", "Bar"], created.Layout.Placements.Select(placement => placement.Display));

        var response = await GetAsync(subject: subject);
        Assert.Equal(["Donut", "Bar"], response.Layout.Placements.Select(placement => placement.Display));
    }

    /// <summary>A placement that says nothing about its shape is a card, so an older caller still works.</summary>
    [Fact]
    public async Task CreateView_WithNoShapeGiven_IsACard()
    {
        var created = await CreateViewAsync(
            "Unshaped", [new { type = "SlaHealth", width = "Third" }], subject: Subject());

        Assert.Equal("Card", Assert.Single(created.Layout.Placements).Display);
    }

    /// <summary>
    /// The `Enum.IsDefined` guard again, on the third member of a placement: a shape that is not a member
    /// must not be stored, because the browser would have to draw something and there is nothing to draw.
    /// </summary>
    [Theory]
    [InlineData("7")]
    [InlineData("Sunburst")]
    public async Task CreateView_WithAShapeThatIsNotDefined_Is400(string display)
    {
        using var request = DashboardHostFixture.Authenticate(
            new HttpRequestMessage(HttpMethod.Post, "/api/dashboard/views"), subject: Subject());
        request.Content = JsonContent.Create(new
        {
            name = "Bad shape",
            placements = new[] { new { type = "SlaHealth", width = "Third", display } },
        });

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDto>();
        Assert.Contains(problem!.Errors!, error => error.Key.EndsWith("display", StringComparison.Ordinal));
    }

    /// <summary>
    /// The failure path, and the third sighting of the standing hazard: <c>Enum.TryParse</c> accepts any
    /// integer, so <c>"99"</c> would parse to a widget that does not exist and be stored in somebody's view.
    /// <c>Enum.IsDefined</c> is the guard, and the answer is a 400 naming every widget rather than a view
    /// with a card nothing can draw.
    /// </summary>
    [Theory]
    [InlineData("99", "Full")]
    [InlineData("NotAWidget", "Full")]
    [InlineData("SlaHealth", "42")]
    [InlineData("SlaHealth", "Enormous")]
    public async Task CreateView_WithAWidgetOrWidthThatIsNotDefined_Is400(string type, string width)
    {
        using var request = DashboardHostFixture.Authenticate(
            new HttpRequestMessage(HttpMethod.Post, "/api/dashboard/views"), subject: Subject());
        request.Content = JsonContent.Create(new
        {
            name = "Broken",
            placements = new[] { new { type, width } },
        });

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDto>();
        Assert.NotNull(problem?.Errors);
        Assert.NotEmpty(problem.Errors);
    }

    /// <summary>
    /// A widget cannot be placed twice. The layout is an ordering of the widgets, and two places for one
    /// card is a request with no meaning rather than one to guess at.
    /// </summary>
    [Fact]
    public async Task CreateView_PlacingOneWidgetTwice_Is400()
    {
        using var request = DashboardHostFixture.Authenticate(
            new HttpRequestMessage(HttpMethod.Post, "/api/dashboard/views"), subject: Subject());
        request.Content = JsonContent.Create(new
        {
            name = "Doubled",
            placements = new[]
            {
                new { type = "SlaHealth", width = "Half" },
                new { type = "SlaHealth", width = "Half" },
            },
        });

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDto>();
        Assert.Contains(problem!.Errors!, error => error.Value.Any(message => message.Contains("more than once")));
    }

    /// <summary>
    /// An end user reaching the endpoint is told nothing about the estate: every widget comes back
    /// forbidden rather than empty, and there is no layout to draw. The distinction is WP-5.4's — an empty
    /// licence-compliance card would be a claim about the estate, and this is a fact about the account.
    /// </summary>
    [Fact]
    public async Task Get_AsAnEndUser_ReportsEveryWidgetForbiddenRatherThanEmpty()
    {
        var response = await GetAsync(role: "EndUser", subject: Subject());

        Assert.All(response.Widgets, widget => Assert.Equal("NotPermitted", widget.Status));
        Assert.All(response.Widgets, widget => Assert.Empty(widget.Segments));
        Assert.Empty(response.Layout.Placements);
    }

    [Fact]
    public async Task Get_WithoutAToken_Is401()
    {
        using var response = await _client.GetAsync("/api/dashboard");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// The one number this suite can assert exactly, and it is asserted as a delta because the estate is
    /// shared: raising a Critical ticket moves the Critical band by exactly one, and the ticket itself
    /// appears in the rows with a link to its own record.
    /// </summary>
    [Fact]
    public async Task OpenByPriority_AfterRaisingACriticalTicket_CountsItAndNamesIt()
    {
        var before = Widget(await GetAsync(), DashboardWidgetType.OpenByPriority);
        var criticalBefore = Segment(before, "Critical");

        var ticket = await RaiseCriticalTicketAsync();

        var after = Widget(await GetAsync(), DashboardWidgetType.OpenByPriority);
        Assert.Equal(criticalBefore + 1, Segment(after, "Critical"));
        Assert.Equal(before.Headline + 1, after.Headline);

        var row = Assert.Single(after.Rows, item => item.Title == ticket.Title);
        Assert.Equal("Critical", row.Badge);
        Assert.Equal("Critical", row.Tone);
        Assert.Equal("Ticket", row.Link!.Target);
        Assert.Equal(ticket.Id, row.Link.RecordId);
        Assert.Contains(ticket.Number, row.Subtitle);
    }

    /// <summary>
    /// Every headline carries a tone, which is the widget's judgement about whether its own number is good
    /// news. The browser owns the colour; without the tone every card would be five identical black
    /// numbers, which is what made the first cut of this screen unreadable at a glance.
    /// </summary>
    [Fact]
    public async Task EveryWidget_SaysWhetherItsHeadlineIsGoodNews()
    {
        var response = await GetAsync();

        Assert.All(response.Widgets, widget => Assert.Contains(
            widget.HeadlineTone, new[] { "Neutral", "Ok", "Warning", "Critical", "Info" }));

        // The one that can be pinned exactly on an estate this test does not control: open work is neither
        // good nor bad news, so it is drawn in the reading colour rather than in a warning one.
        Assert.Equal("Neutral", Widget(response, DashboardWidgetType.OpenByPriority).HeadlineTone);
    }

    /// <summary>
    /// The deep link every band carries — the WP's third verification step. Asserted on the server side as
    /// a target and a domain-spelt filter; turning the pair into a route is the browser's job, and
    /// deliberately not repeated here.
    /// </summary>
    [Fact]
    public async Task EveryWidget_CarriesADeepLinkForItsListAndForEachOfItsBands()
    {
        var response = await GetAsync();

        foreach (var widget in response.Widgets)
        {
            Assert.NotNull(widget.Link);
            Assert.All(widget.Rows, row => Assert.NotNull(row.Link));
        }

        var priorities = Widget(response, DashboardWidgetType.OpenByPriority);
        Assert.All(priorities.Segments, segment =>
        {
            Assert.Equal("TicketList", segment.Link!.Target);
            Assert.NotNull(segment.Link.Filter);
        });

        var compliance = Widget(response, DashboardWidgetType.LicenseCompliance);
        Assert.All(compliance.Segments, segment => Assert.Equal("SoftwareCompliance", segment.Link!.Target));
    }

    /// <summary>
    /// The SLA clock read through the widget, against a policy and a calendar that really came out of the
    /// database.
    /// <para>
    /// This exists because of a specific trap rather than for symmetry: EF <b>ignores an Include</b> once a
    /// query projects a shape other than the entity it started from, so a widget written the obvious way
    /// gets a null policy and throws on its first row — and this dashboard would report that as one card
    /// that could not be loaded, which is a very quiet way to lose a feature. The assertion that the card
    /// is <c>Loaded</c> and names the breach is what catches it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task SlaHealth_ForABreachedSla_CountsItAndSaysHowFarPastTheTargetItIs()
    {
        var ticket = await RaiseLowPriorityTicketAsync();
        await WriteBreachedSlaAsync(ticket.Id, overrunDays: 3);

        var widget = Widget(await GetAsync(), DashboardWidgetType.SlaHealth);

        Assert.Equal("Loaded", widget.Status);
        Assert.True(Segment(widget, "Breached") >= 1);
        Assert.True(widget.Headline >= 1);

        var row = Assert.Single(widget.Rows, item => item.Link?.RecordId == ticket.Id);
        Assert.Equal("Breached by 3d", row.Badge);
        Assert.Equal("Critical", row.Tone);
        // A breached SLA is due "immediately" by construction, so no date rides beside it.
        Assert.Null(row.At);
    }

    /// <summary>
    /// WP-5.1's correlation, read as a list: an alert that explains another appears, and says how many it
    /// explains. Written straight to the table — the correlation itself is the alert engine's own suite —
    /// and asserted by looking this pair up rather than by counting, because the window is estate-wide.
    /// </summary>
    [Fact]
    public async Task RecentRootCauses_ForAnAlertThatExplainsAnother_NamesItAndCountsWhatItExplains()
    {
        var (causeId, address) = await WriteCorrelatedAlertsAsync();

        var widget = Widget(await GetAsync(), DashboardWidgetType.RecentRootCauses);

        var row = Assert.Single(widget.Rows, item => item.Link?.RecordId == causeId);
        Assert.Equal("Critical", row.Badge);
        Assert.Equal("Alert", row.Link!.Target);
        Assert.Contains(address, row.Subtitle);
        Assert.Contains("explains 2 alerts", row.Subtitle);
    }

    private async Task<DashboardDto> GetAsync(string role = "Technician", string? subject = null)
    {
        using var request = DashboardHostFixture.Authenticate(
            new HttpRequestMessage(HttpMethod.Get, "/api/dashboard"), role, subject);
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<DashboardDto>())!;
    }

    private async Task<ViewStateDto> CreateViewAsync(
        string name,
        object[] placements,
        string role = "Technician",
        string? subject = null)
    {
        using var request = DashboardHostFixture.Authenticate(
            new HttpRequestMessage(HttpMethod.Post, "/api/dashboard/views"), role, subject);
        request.Content = JsonContent.Create(new { name, placements });
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ViewStateDto>())!;
    }

    private async Task<ViewStateDto> SaveViewAsync(
        Guid viewId,
        object body,
        string role = "Technician",
        string? subject = null)
    {
        using var request = DashboardHostFixture.Authenticate(
            new HttpRequestMessage(HttpMethod.Put, $"/api/dashboard/views/{viewId}"), role, subject);
        request.Content = JsonContent.Create(body);
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ViewStateDto>())!;
    }

    private async Task<TicketDto> RaiseCriticalTicketAsync()
    {
        using var request = DashboardHostFixture.Authenticate(
            new HttpRequestMessage(HttpMethod.Post, "/api/tickets"), subject: Subject());
        request.Content = JsonContent.Create(new
        {
            title = $"Dashboard critical {Guid.NewGuid():N}",
            description = "Raised by the unified dashboard integration test.",
            type = "Incident",
            // High urgency and high impact is the only pair the matrix calls Critical (WP-1.2).
            urgency = "High",
            impact = "High",
        });
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<TicketDto>())!;
    }

    private async Task<TicketDto> RaiseLowPriorityTicketAsync()
    {
        using var request = DashboardHostFixture.Authenticate(
            new HttpRequestMessage(HttpMethod.Post, "/api/tickets"), subject: Subject());
        request.Content = JsonContent.Create(new
        {
            title = $"Dashboard SLA {Guid.NewGuid():N}",
            description = "Raised by the unified dashboard integration test.",
            type = "Incident",
            urgency = "Low",
            impact = "Low",
        });
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<TicketDto>())!;
    }

    /// <summary>
    /// An SLA that is a known distance past its target, written straight to the tables.
    /// <para>
    /// Two details are deliberate and both are about not disturbing the forty other classes sharing this
    /// database. The policy is created <b>inactive</b>, so <c>SlaService.StartAsync</c> will never pick it
    /// up for anybody else's ticket — an active policy here would silently attach an SLA to every Low
    /// ticket the rest of the suite raises. And the clock is <b>paused</b> (<c>ActiveSince</c> null), so the
    /// elapsed time is exactly what is banked and the overrun is a fixed number rather than a race with the
    /// wall clock.
    /// </para>
    /// </summary>
    private async Task WriteBreachedSlaAsync(Guid ticketId, int overrunDays)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var helpdesk = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();

        var calendar = new BusinessHoursCalendar
        {
            Id = Guid.CreateVersion7(),
            Name = $"Dashboard tests {Guid.NewGuid():N}"[..40],
            TimeZoneId = "UTC",
            WorkingDays = BusinessDays.Weekdays | BusinessDays.Saturday | BusinessDays.Sunday,
            StartTime = new TimeOnly(0, 0),
            EndTime = new TimeOnly(23, 59),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var policy = new SlaPolicy
        {
            Id = Guid.CreateVersion7(),
            Name = $"Dashboard tests {Guid.NewGuid():N}"[..40],
            Priority = TicketPriority.Low,
            ResolutionTargetMinutes = 60,
            ResponseTargetMinutes = 30,
            WarningPercent = 80,
            CalendarId = calendar.Id,
            IsActive = false,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        helpdesk.BusinessHoursCalendars.Add(calendar);
        helpdesk.SlaPolicies.Add(policy);
        helpdesk.TicketSlas.Add(new TicketSla
        {
            Id = Guid.CreateVersion7(),
            TicketId = ticketId,
            PolicyId = policy.Id,
            StartedAt = DateTimeOffset.UtcNow.AddDays(-overrunDays - 1),
            ActiveSince = null,
            AccumulatedBusinessSeconds = (policy.ResolutionTargetMinutes * 60d) + (overrunDays * 86_400d),
        });
        await helpdesk.SaveChangesAsync();
    }

    /// <summary>
    /// One cause and two consequences filed under it, written to the table directly. What is under test is
    /// the read; how an alert comes to have a root cause is <c>AlertCorrelatorTests</c>' subject.
    /// </summary>
    private async Task<(Guid CauseId, string Address)> WriteCorrelatedAlertsAsync()
    {
        await using var scope = host.Services.CreateAsyncScope();
        var monitoring = scope.ServiceProvider.GetRequiredService<MonitoringDbContext>();

        var address = $"10.88.{Random.Shared.Next(1, 250)}.{Random.Shared.Next(2, 250)}";
        var deviceId = Guid.CreateVersion7();
        var ciId = Guid.CreateVersion7();
        monitoring.MonitoredDevices.Add(new MonitoredDevice
        {
            Id = deviceId,
            CiId = ciId,
            Address = address,
            PollerGroup = "default",
            CreatedBy = "dashboard-tests",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedBy = "dashboard-tests",
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        var causeId = Guid.CreateVersion7();
        var raisedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        monitoring.Alerts.Add(new Alert
        {
            Id = causeId,
            DeviceId = deviceId,
            CiId = ciId,
            CheckId = Guid.CreateVersion7(),
            RuleId = $"check:{causeId:N}:availability",
            MetricName = "check.success",
            Severity = AlertSeverity.Critical,
            Status = AlertStatus.Open,
            Summary = "Core switch did not answer ICMP",
            RaisedAt = raisedAt,
            LastObservedAt = DateTimeOffset.UtcNow,
            PollerName = "dashboard-tests",
        });

        foreach (var index in Enumerable.Range(0, 2))
        {
            monitoring.Alerts.Add(new Alert
            {
                Id = Guid.CreateVersion7(),
                DeviceId = deviceId,
                CiId = Guid.CreateVersion7(),
                CheckId = Guid.CreateVersion7(),
                RuleId = $"check:{causeId:N}:consequence-{index}",
                MetricName = "check.success",
                Severity = AlertSeverity.Warning,
                Status = AlertStatus.Open,
                Summary = $"Dependent {index} did not answer ICMP",
                Suppression = AlertSuppression.RootCause,
                RootCauseAlertId = causeId,
                RaisedAt = raisedAt.AddSeconds(30),
                LastObservedAt = DateTimeOffset.UtcNow,
                PollerName = "dashboard-tests",
            });
        }

        await monitoring.SaveChangesAsync();
        return (causeId, address);
    }

    private static WidgetDto Widget(DashboardDto dashboard, DashboardWidgetType type) =>
        Assert.Single(dashboard.Widgets, widget => widget.Type == type.ToString());

    private static int Segment(WidgetDto widget, string label) =>
        Assert.Single(widget.Segments, segment => segment.Label == label).Value;

    /// <summary>
    /// A subject nobody else uses. A saved layout belongs to a person, so a test sharing a subject with its
    /// neighbours would assert against whatever the last one arranged.
    /// </summary>
    private static string Subject() => $"dashboard-tests-{Guid.NewGuid():N}";

    private sealed record DashboardDto(
        LayoutDto Layout, IReadOnlyList<ViewDto> Views, IReadOnlyList<WidgetDto> Widgets);

    /// <summary>What every write answers with: the views that now exist and the layout now on screen.</summary>
    private sealed record ViewStateDto(LayoutDto Layout, IReadOnlyList<ViewDto> Views);

    private sealed record ViewDto(Guid Id, string Name, bool IsActive, DateTimeOffset UpdatedAt);

    private sealed record LayoutDto(
        string Source,
        Guid? ViewId,
        string? Name,
        string Preset,
        DateTimeOffset? SavedAt,
        IReadOnlyList<PlacementDto> Placements);

    private sealed record PlacementDto(string Type, string Width, string Display);

    private sealed record WidgetDto(
        string Type,
        string Status,
        string Title,
        string? Subtitle,
        int? Headline,
        string? HeadlineLabel,
        string HeadlineTone,
        IReadOnlyList<SegmentDto> Segments,
        IReadOnlyList<RowDto> Rows,
        int RowTotal,
        bool RowsTruncated,
        LinkDto? Link);

    private sealed record SegmentDto(string Label, int Value, string Tone, LinkDto? Link);

    private sealed record RowDto(
        string Title, string? Subtitle, string? Badge, string Tone, LinkDto? Link, DateTimeOffset? At);

    private sealed record LinkDto(string Target, string? Filter, Guid? RecordId);

    private sealed record TicketDto(Guid Id, string Number, string Title);

    private sealed record ProblemDto(string? Title, Dictionary<string, string[]>? Errors);
}
