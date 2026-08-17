using System.Net;
using System.Net.Http.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Modules.Helpdesk.Data;
using Modules.Helpdesk.Seeding;

using Platform.Data;

namespace Infrastructure.Tests;

/// <summary>
/// Recurrence detection end to end (WP-5.7), which is the WP's first verification step: incidents pile up
/// on one switch, a suggestion appears, and somebody turns it into a problem with the incidents attached.
/// <para>
/// The database is shared with the whole suite, so a pass here counts other classes' incidents too. Every
/// assertion below is therefore about a CI or a category this class created for itself, and the pass is
/// asked for explicitly rather than left to the job — the scheduler is off in this host.
/// </para>
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class ProblemDetectionIntegrationTests(InfrastructureFixture infrastructure, ProblemHostFixture host)
    : IClassFixture<ProblemHostFixture>, IAsyncLifetime
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
    /// The WP's verification, whole: incidents on one CI produce a suggestion, accepting it creates a
    /// problem, and the incidents that caused it arrive attached.
    /// </summary>
    [Fact]
    public async Task Detect_ForEnoughIncidentsOnOneCi_SuggestsAProblemThatCanBeAcceptedWithItsIncidents()
    {
        var ci = await CreateCiAsync("Recurring branch switch");
        var incidents = await IncidentsOnCiAsync(ci, ProblemHostFixture.MinimumIncidents);

        var run = await DetectAsync();
        Assert.Equal(ProblemHostFixture.MinimumIncidents, run.MinimumIncidents);

        var suggestion = await FindOpenSuggestionAsync(ci);
        Assert.Equal("Ci", suggestion.Scope);
        Assert.Equal(ci, suggestion.Subject.Id);
        // The CI's name is read live through the port, never snapshotted.
        Assert.StartsWith("Recurring branch switch", suggestion.Subject.Name, StringComparison.Ordinal);
        Assert.Equal(ProblemHostFixture.MinimumIncidents, suggestion.IncidentCount);

        using var accepted = await host.PostAsync($"/api/problem-suggestions/{suggestion.Id}/acceptance", null);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var answered = Assert.IsType<SuggestionDto>(await accepted.Content.ReadFromJsonAsync<SuggestionDto>());

        Assert.Equal("Accepted", answered.Status);
        Assert.NotNull(answered.CreatedProblemId);
        Assert.StartsWith("PRB-", answered.CreatedProblemNumber, StringComparison.Ordinal);

        var problem = await host.GetAsync<ProblemApiIntegrationTests.ProblemDto>(
            $"/api/problems/{answered.CreatedProblemId}");
        Assert.Equal(ProblemHostFixture.MinimumIncidents, problem.IncidentCount);
        Assert.Equal(
            incidents.Select(incident => incident.Number).Order().ToArray(),
            problem.Incidents!.Select(incident => incident.Number).Order().ToArray());
        Assert.Equal("Ci", problem.Subject!.Scope);
        Assert.Equal(ci, problem.Subject.Id);
        // The default title is composed from what the pass counted, so accepting is one click.
        Assert.Contains("Recurring incidents on", problem.Title, StringComparison.Ordinal);
    }

    /// <summary>One short of the threshold says nothing at all, which is most of what the pass does.</summary>
    [Fact]
    public async Task Detect_ForOneShortOfTheThreshold_RaisesNoSuggestion()
    {
        var ci = await CreateCiAsync("Quiet switch");
        await IncidentsOnCiAsync(ci, ProblemHostFixture.MinimumIncidents - 1);

        await DetectAsync();

        Assert.DoesNotContain(await OpenSuggestionsAsync(), suggestion => suggestion.Subject.Id == ci);
    }

    /// <summary>
    /// Idempotence, which is what lets the job start at host start-up and the manual run be pressed twice.
    /// The filtered unique index is the thing that makes it true rather than the ordering.
    /// </summary>
    [Fact]
    public async Task Detect_RunTwice_RaisesTheSuggestionOnlyOnce()
    {
        var ci = await CreateCiAsync("Twice-counted switch");
        await IncidentsOnCiAsync(ci, ProblemHostFixture.MinimumIncidents);

        await DetectAsync();
        var second = await DetectAsync();

        Assert.Single(await OpenSuggestionsAsync(), suggestion => suggestion.Subject.Id == ci);
        Assert.DoesNotContain(second.Suggestions, suggestion => suggestion.Subject.Id == ci);
        Assert.True(second.Skipped.TryGetValue("AlreadySuggested", out var skipped) && skipped >= 1);
    }

    /// <summary>
    /// Once somebody is working the problem, the pass stops restating it. Without this the inbox fills up
    /// with the recurrence somebody is already fixing, which is the fastest way to make it ignored.
    /// </summary>
    [Fact]
    public async Task Detect_ForASubjectWithAnOpenProblem_StaysQuiet()
    {
        var ci = await CreateCiAsync("Already being worked");
        await IncidentsOnCiAsync(ci, ProblemHostFixture.MinimumIncidents);
        await DetectAsync();
        var suggestion = await FindOpenSuggestionAsync(ci);
        using var accepted = await host.PostAsync($"/api/problem-suggestions/{suggestion.Id}/acceptance", null);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

        // More incidents arrive, and the pass runs again.
        await IncidentsOnCiAsync(ci, 2);
        var run = await DetectAsync();

        Assert.DoesNotContain(run.Suggestions, item => item.Subject.Id == ci);
        Assert.DoesNotContain(await OpenSuggestionsAsync(), item => item.Subject.Id == ci);
    }

    /// <summary>
    /// A dismissal has to mean something, or dismissing is a button that works until the next pass.
    /// </summary>
    [Fact]
    public async Task Detect_AfterASuggestionWasDismissed_StaysQuietForTheCooldown()
    {
        var ci = await CreateCiAsync("Dismissed switch");
        await IncidentsOnCiAsync(ci, ProblemHostFixture.MinimumIncidents);
        await DetectAsync();
        var suggestion = await FindOpenSuggestionAsync(ci);

        using var dismissed = await host.PostAsync(
            $"/api/problem-suggestions/{suggestion.Id}/dismissal",
            new { reason = "Three unrelated faults that happened to share a rack." });
        Assert.Equal(HttpStatusCode.OK, dismissed.StatusCode);
        var answered = Assert.IsType<SuggestionDto>(await dismissed.Content.ReadFromJsonAsync<SuggestionDto>());
        Assert.Equal("Dismissed", answered.Status);
        Assert.Equal("Three unrelated faults that happened to share a rack.", answered.DismissReason);

        var run = await DetectAsync();

        Assert.DoesNotContain(run.Suggestions, item => item.Subject.Id == ci);
        Assert.True(run.Skipped.TryGetValue("DismissalStillHolds", out var skipped) && skipped >= 1);
    }

    /// <summary>
    /// And it stops meaning something eventually. The cooldown is measured from when somebody answered, so
    /// backdating that answer is what proves the rule rather than waiting a week.
    /// </summary>
    [Fact]
    public async Task Detect_AfterTheDismissalCooldownHasPassed_SuggestsAgain()
    {
        var ci = await CreateCiAsync("Dismissed long ago");
        await IncidentsOnCiAsync(ci, ProblemHostFixture.MinimumIncidents);
        await DetectAsync();
        var suggestion = await FindOpenSuggestionAsync(ci);
        using var dismissed = await host.PostAsync($"/api/problem-suggestions/{suggestion.Id}/dismissal", null);
        Assert.Equal(HttpStatusCode.OK, dismissed.StatusCode);

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var helpdesk = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            await helpdesk.ProblemSuggestions
                .Where(item => item.Id == suggestion.Id)
                .ExecuteUpdateAsync(setter => setter.SetProperty(
                    item => item.ResolvedAt, DateTimeOffset.UtcNow.AddDays(-30)));
        }

        var run = await DetectAsync();

        Assert.Contains(run.Suggestions, item => item.Subject.Id == ci);
    }

    /// <summary>The other grouping the WP names: a category with no particular machine behind it.</summary>
    [Fact]
    public async Task Detect_ForEnoughIncidentsInOneCategory_SuggestsAgainstTheCategory()
    {
        var categoryName = $"Recurring category {Guid.NewGuid():N}";
        var category = await CreateCategoryAsync(categoryName);
        for (var index = 0; index < ProblemHostFixture.MinimumIncidents; index++)
        {
            await CreateIncidentAsync($"Password reset {index}", categoryId: category);
        }

        await DetectAsync();

        var suggestion = Assert.Single(await OpenSuggestionsAsync(), item => item.Subject.Id == category);
        Assert.Equal("Category", suggestion.Scope);
        Assert.Equal(categoryName, suggestion.Subject.Name);

        using var accepted = await host.PostAsync($"/api/problem-suggestions/{suggestion.Id}/acceptance", null);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var answered = Assert.IsType<SuggestionDto>(await accepted.Content.ReadFromJsonAsync<SuggestionDto>());
        var problem = await host.GetAsync<ProblemApiIntegrationTests.ProblemDto>(
            $"/api/problems/{answered.CreatedProblemId}");

        Assert.Equal("Category", problem.Subject!.Scope);
        Assert.Equal(ProblemHostFixture.MinimumIncidents, problem.IncidentCount);
    }

    /// <summary>
    /// Incidents that arrived while the suggestion sat in the inbox belong to the same recurrence. A
    /// problem that opened already under-counting its own evidence is one nobody trusts.
    /// </summary>
    [Fact]
    public async Task Accept_LinksIncidentsThatArrivedAfterTheSuggestionWasRaised()
    {
        var ci = await CreateCiAsync("Still failing while we look");
        await IncidentsOnCiAsync(ci, ProblemHostFixture.MinimumIncidents);
        await DetectAsync();
        var suggestion = await FindOpenSuggestionAsync(ci);
        await IncidentsOnCiAsync(ci, 2);

        using var accepted = await host.PostAsync($"/api/problem-suggestions/{suggestion.Id}/acceptance", null);
        var answered = Assert.IsType<SuggestionDto>(await accepted.Content.ReadFromJsonAsync<SuggestionDto>());
        var problem = await host.GetAsync<ProblemApiIntegrationTests.ProblemDto>(
            $"/api/problems/{answered.CreatedProblemId}");

        Assert.Equal(ProblemHostFixture.MinimumIncidents + 2, problem.IncidentCount);
    }

    /// <summary>Accepting with a title and a priority of one's own, which is the other way in.</summary>
    [Fact]
    public async Task Accept_WithAnEditedTitle_UsesIt()
    {
        var ci = await CreateCiAsync("Renamed on accept");
        await IncidentsOnCiAsync(ci, ProblemHostFixture.MinimumIncidents);
        await DetectAsync();
        var suggestion = await FindOpenSuggestionAsync(ci);

        using var accepted = await host.PostAsync($"/api/problem-suggestions/{suggestion.Id}/acceptance", new
        {
            title = "Branch switch power supply is failing",
            priority = "Critical",
        });
        var answered = Assert.IsType<SuggestionDto>(await accepted.Content.ReadFromJsonAsync<SuggestionDto>());
        var problem = await host.GetAsync<ProblemApiIntegrationTests.ProblemDto>(
            $"/api/problems/{answered.CreatedProblemId}");

        Assert.Equal("Branch switch power supply is failing", problem.Title);
        Assert.Equal("Critical", problem.Priority);
    }

    /// <summary>Failure path: a suggestion is answered once.</summary>
    [Fact]
    public async Task Accept_ASuggestionAlreadyAnswered_IsAConflict()
    {
        var ci = await CreateCiAsync("Answered twice");
        await IncidentsOnCiAsync(ci, ProblemHostFixture.MinimumIncidents);
        await DetectAsync();
        var suggestion = await FindOpenSuggestionAsync(ci);
        using var first = await host.PostAsync($"/api/problem-suggestions/{suggestion.Id}/acceptance", null);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        using var second = await host.PostAsync($"/api/problem-suggestions/{suggestion.Id}/acceptance", null);
        var body = await second.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("application/problem+json", second.Content.Headers.ContentType?.MediaType);
        Assert.Contains("already made a problem", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Accept_ASuggestionThatDoesNotExist_IsNotFound()
    {
        using var response = await host.PostAsync(
            $"/api/problem-suggestions/{Guid.CreateVersion7()}/acceptance", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>The inbox is an agent surface, and so is running the pass.</summary>
    [Fact]
    public async Task Suggestions_AsAnEndUser_AreEmptyAndTheRunIsForbidden()
    {
        Assert.Empty(await host.GetAsync<List<SuggestionDto>>("/api/problem-suggestions", role: "EndUser"));

        using var response = await host.PostAsync("/api/problem-suggestions/detect", null, "EndUser");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    /// <summary>The same three-clause enum guard, on the inbox's own filter.</summary>
    [Theory]
    [InlineData("1")]
    [InlineData("Pending")]
    public async Task ListSuggestions_WithAStatusThatIsNotSpelt_IsRefused(string status)
    {
        using var request = host.Request(HttpMethod.Get, $"/api/problem-suggestions?status={status}");
        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>Every suggestion the pass raises is audited, under the identity that asked for the pass.</summary>
    [Fact]
    public async Task Detect_AuditsEverySuggestionItRaises()
    {
        var ci = await CreateCiAsync("Audited recurrence");
        await IncidentsOnCiAsync(ci, ProblemHostFixture.MinimumIncidents);
        await DetectAsync();
        var suggestion = await FindOpenSuggestionAsync(ci);

        await using var scope = host.Services.CreateAsyncScope();
        var platform = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var entityId = suggestion.Id.ToString();
        var entry = Assert.Single(await platform.AuditEntries
            .Where(item => item.EntityType == "ProblemSuggestion" && item.EntityId == entityId)
            .ToListAsync());

        Assert.Equal("Suggested", entry.Action);
        Assert.Equal(ProblemHostFixture.ProblemAuthenticationHandler.ActorId, entry.ActorId);
    }

    /// <summary>
    /// The seeded recurrence, which is what makes this feature walkable a minute after <c>aspire run</c>.
    /// Seeding exactly the default threshold is deliberate — if either number moves, this fails.
    /// </summary>
    [Fact]
    public async Task ProblemRecurrenceSeeder_SeedsExactlyTheDefaultThresholdOnOneCiAndIsIdempotent()
    {
        var ciId = await CreateCiAsync("Seeded recurrence target");

        await using var scope = host.Services.CreateAsyncScope();
        var helpdesk = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
        var seeder = new ProblemRecurrenceSeeder(helpdesk);

        var first = await seeder.SeedAsync([ciId]);
        Assert.Equal(ProblemRecurrenceSeeder.IncidentCount, first.TicketsAdded);
        Assert.Equal(ProblemRecurrenceSeeder.IncidentCount, first.LinksAdded);
        Assert.Equal(ciId, first.CiId);

        var second = await seeder.SeedAsync([ciId]);
        Assert.Equal(0, second.TicketsAdded);
        Assert.Equal(0, second.LinksAdded);

        // All five on the one CI, inside the detector's default window, and all incidents.
        var linked = await helpdesk.TicketCiLinks.AsNoTracking()
            .Where(link => link.CiId == ciId)
            .Select(link => new { link.Ticket.Type, link.Ticket.CreatedAt })
            .ToListAsync();
        Assert.Equal(ProblemRecurrenceSeeder.IncidentCount, linked.Count);
        Assert.All(linked, row => Assert.Equal(TicketType.Incident, row.Type));
        Assert.All(linked, row => Assert.True(row.CreatedAt > DateTimeOffset.UtcNow.AddDays(-7)));
    }

    /// <summary>An estate with no network devices seeds nothing rather than throwing.</summary>
    [Fact]
    public async Task ProblemRecurrenceSeeder_WithNoNetworkDevices_SeedsNothing()
    {
        await using var scope = host.Services.CreateAsyncScope();
        var helpdesk = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();

        var result = await new ProblemRecurrenceSeeder(helpdesk).SeedAsync([]);

        Assert.Equal(0, result.TicketsAdded);
        Assert.Null(result.CiId);
    }

    // ---- helpers ----

    private async Task<DetectionRunDto> DetectAsync()
    {
        using var response = await host.PostAsync("/api/problem-suggestions/detect", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Assert.IsType<DetectionRunDto>(await response.Content.ReadFromJsonAsync<DetectionRunDto>());
    }

    private async Task<List<SuggestionDto>> OpenSuggestionsAsync() =>
        await host.GetAsync<List<SuggestionDto>>("/api/problem-suggestions?status=Open");

    private async Task<SuggestionDto> FindOpenSuggestionAsync(Guid subjectId) =>
        Assert.Single(await OpenSuggestionsAsync(), suggestion => suggestion.Subject.Id == subjectId);

    private async Task<List<TicketDto>> IncidentsOnCiAsync(Guid ciId, int count)
    {
        var incidents = new List<TicketDto>();
        for (var index = 0; index < count; index++)
        {
            var incident = await CreateIncidentAsync($"Recurring fault {index} {Guid.NewGuid():N}");
            using var link = host.Request(HttpMethod.Post, $"/api/tickets/{incident.Id}/cis");
            link.Content = JsonContent.Create(new { ciId });
            using var linked = await _client.SendAsync(link);
            Assert.Equal(HttpStatusCode.Created, linked.StatusCode);
            incidents.Add(incident);
        }

        return incidents;
    }

    private async Task<TicketDto> CreateIncidentAsync(string title, Guid? categoryId = null)
    {
        using var response = await host.PostAsync("/api/tickets", new
        {
            title,
            description = "Raised by the recurrence detection integration test.",
            type = "Incident",
            urgency = "Medium",
            impact = "Medium",
            categoryId,
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return Assert.IsType<TicketDto>(await response.Content.ReadFromJsonAsync<TicketDto>());
    }

    private async Task<Guid> CreateCiAsync(string name)
    {
        using var response = await host.PostAsync("/api/cis", new
        {
            type = "NetworkDevice",
            name = $"{name} {Guid.NewGuid():N}",
            attributes = new Dictionary<string, string>
            {
                ["managementIp"] = "10.0.0.2",
                ["vendor"] = "Cisco",
                ["portCount"] = "24",
            },
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return Assert.IsType<CiDto>(await response.Content.ReadFromJsonAsync<CiDto>()).Id;
    }

    private async Task<Guid> CreateCategoryAsync(string name)
    {
        using var response = await host.PostAsync(
            "/api/ticket-categories", new { name, parentId = (Guid?)null }, "Admin");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return Assert.IsType<CategoryDto>(await response.Content.ReadFromJsonAsync<CategoryDto>()).Id;
    }

    private sealed record TicketDto(Guid Id, string Number, string Title, string Status);

    private sealed record CiDto(Guid Id, string Type, string Name);

    private sealed record CategoryDto(Guid Id, string Name);

    private sealed record SuggestionDto(
        Guid Id,
        string Scope,
        ProblemApiIntegrationTests.SubjectDto Subject,
        int IncidentCount,
        DateTimeOffset WindowStart,
        DateTimeOffset WindowEnd,
        string Status,
        DateTimeOffset DetectedAt,
        Guid? CreatedProblemId,
        string? CreatedProblemNumber,
        string? ResolvedById,
        string? ResolvedByName,
        DateTimeOffset? ResolvedAt,
        string? DismissReason);

    private sealed record DetectionRunDto(
        DateTimeOffset WindowStart,
        DateTimeOffset WindowEnd,
        int MinimumIncidents,
        int Examined,
        int Suggested,
        Dictionary<string, int> Skipped,
        List<SuggestionDto> Suggestions);
}
