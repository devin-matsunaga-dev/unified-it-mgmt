using System.Net;
using System.Net.Http.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Platform.Data;

namespace Infrastructure.Tests;

/// <summary>
/// Problem management end to end (WP-5.7): a problem that links incidents, the known error it becomes,
/// and the article draft it prompts for on the way out.
/// <para>
/// The suite shares its database with forty other classes, so nothing here counts anything estate-wide.
/// Every test works on incidents it raised itself and asserts on those.
/// </para>
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class ProblemApiIntegrationTests(InfrastructureFixture infrastructure, ProblemHostFixture host)
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
    /// The WP's second verification step in its direct form: a problem is created and the incidents it
    /// explains are linked to it, readable from both ends.
    /// </summary>
    [Fact]
    public async Task CreateProblem_WithIncidents_LinksThemAndTheyReadBackFromBothSides()
    {
        var first = await CreateIncidentAsync("Uplink drops at 09:00");
        var second = await CreateIncidentAsync("Uplink drops again mid-morning");

        var problem = await CreateProblemAsync("Second floor uplink flaps", incidents: [first.Id, second.Id]);

        Assert.StartsWith("PRB-", problem.Number, StringComparison.Ordinal);
        Assert.Equal("Investigating", problem.Status);
        Assert.False(problem.IsKnownError);
        Assert.Equal(2, problem.IncidentCount);

        var read = await host.GetAsync<ProblemDto>($"/api/problems/{problem.Id}");
        Assert.Equal(
            new[] { first.Number, second.Number }.Order().ToArray(),
            read.Incidents!.Select(incident => incident.Number).Order().ToArray());

        // And from the incident, which is how a technician holding a fresh ticket learns it is not alone.
        var fromTicket = await host.GetAsync<List<ProblemDto>>($"/api/tickets/{first.Id}/problems");
        Assert.Equal(problem.Id, Assert.Single(fromTicket).Id);
    }

    /// <summary>
    /// The entry condition that makes the known-error database a database. This is the failure path the
    /// WP-5.7 workflow exists for: a known error with nothing to say would be an open problem with a
    /// longer name.
    /// </summary>
    [Fact]
    public async Task Transition_ToKnownError_WithoutAWorkaround_IsRefusedWithAFieldError()
    {
        var problem = await CreateProblemAsync("Nobody knows why yet");

        using var response = await host.PostAsync(
            $"/api/problems/{problem.Id}/transitions", new { targetStatus = "KnownError" });
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("root cause and a workaround", body, StringComparison.Ordinal);

        // And it really did not move.
        Assert.Equal("Investigating", (await host.GetAsync<ProblemDto>($"/api/problems/{problem.Id}")).Status);
    }

    /// <summary>The known-error database, populated and then found by the thing somebody would search for.</summary>
    [Fact]
    public async Task Transition_ToKnownError_WithBothHalves_PublishesItToTheKnownErrorList()
    {
        var marker = $"kedb{Guid.NewGuid():N}";
        var problem = await CreateProblemAsync($"Uplink flaps on the {marker} switch");
        await UpdateAsync(problem.Id, new
        {
            title = $"Uplink flaps on the {marker} switch",
            description = "Recurring drops.",
            priority = "High",
            rootCause = "A failing SFP in port 23.",
            workaround = $"Move the affected users to port 24 and note the {marker} tag.",
        });

        using var response = await host.PostAsync(
            $"/api/problems/{problem.Id}/transitions", new { targetStatus = "KnownError" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var transitioned = Assert.IsType<TransitionDto>(await response.Content.ReadFromJsonAsync<TransitionDto>());

        Assert.Equal("KnownError", transitioned.Problem.Status);
        Assert.True(transitioned.Problem.IsKnownError);
        Assert.NotNull(transitioned.Problem.KnownErrorAt);
        // Nothing is offered here: the WP prompts for an article when a problem is closed, not when its
        // cause is found.
        Assert.Null(transitioned.KnowledgeDraft);

        // Findable by the workaround's text, which is what somebody holding a fresh incident would search.
        var found = await host.GetAsync<ProblemPageDto>($"/api/problems?knownErrorsOnly=true&search={marker}");
        Assert.Equal(problem.Id, Assert.Single(found.Items).Id);
    }

    /// <summary>
    /// A known error whose workaround is erased stops being one. Leaving it in place would put a row in
    /// the database that answers nothing, which is the failure mode a known-error list has.
    /// </summary>
    [Fact]
    public async Task Update_ThatErasesTheWorkaround_TakesTheProblemOutOfTheKnownErrorList()
    {
        var problem = await KnownErrorAsync("Erasable known error");

        await UpdateAsync(problem.Id, new
        {
            title = "Erasable known error",
            description = "Recurring drops.",
            priority = "High",
            rootCause = "A failing SFP in port 23.",
            workaround = (string?)null,
        });

        var read = await host.GetAsync<ProblemDto>($"/api/problems/{problem.Id}");
        Assert.Equal("Investigating", read.Status);
        Assert.False(read.IsKnownError);
        Assert.Null(read.KnownErrorAt);
    }

    /// <summary>The WP's third verification step: closing a problem prompts for a knowledge article.</summary>
    [Fact]
    public async Task Transition_ToClosed_AnswersWithAKnowledgeArticleDraft()
    {
        var first = await CreateIncidentAsync("Wi-Fi keeps dropping");
        var second = await CreateIncidentAsync("Wi-Fi keeps dropping");
        var third = await CreateIncidentAsync("Video calls cut out");
        var problem = await CreateProblemAsync(
            "Second floor access point", incidents: [first.Id, second.Id, third.Id]);
        await UpdateAsync(problem.Id, new
        {
            title = "Second floor access point",
            description = "Recurring drops.",
            priority = "High",
            rootCause = "A failing radio in the floor 2 access point.",
            workaround = "Associate to the floor 3 access point.",
        });

        using var response = await host.PostAsync($"/api/problems/{problem.Id}/transitions", new
        {
            targetStatus = "Closed",
            resolution = "Access point replaced under warranty.",
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var transitioned = Assert.IsType<TransitionDto>(await response.Content.ReadFromJsonAsync<TransitionDto>());

        Assert.Equal("Closed", transitioned.Problem.Status);
        Assert.NotNull(transitioned.Problem.ClosedAt);
        // Closing implies resolution: a problem cannot be closed and still open in the SLA sense.
        Assert.NotNull(transitioned.Problem.ResolvedAt);

        var draft = Assert.IsType<KnowledgeDraftDto>(transitioned.KnowledgeDraft);
        Assert.Equal(problem.Number, draft.ProblemNumber);
        Assert.Equal("A failing radio in the floor 2 access point.", draft.RootCause);
        Assert.Equal("Associate to the floor 3 access point.", draft.Workaround);
        Assert.Equal("Access point replaced under warranty.", draft.Resolution);
        Assert.Equal(3, draft.IncidentNumbers.Count);
        // The repeated report is one symptom reported twice, not two symptoms.
        var repeated = Assert.Single(draft.Symptoms, symptom => symptom.Text == "Wi-Fi keeps dropping");
        Assert.Equal(2, repeated.IncidentCount);

        // And it is still available afterwards, so nobody has to get the prompt right first time.
        var again = await host.GetAsync<KnowledgeDraftDto>($"/api/problems/{problem.Id}/knowledge-draft");
        Assert.Equal(draft.IncidentNumbers.Count, again.IncidentNumbers.Count);
    }

    /// <summary>Failure path: closing without saying what was done.</summary>
    [Fact]
    public async Task Transition_ToClosed_WithNoResolution_IsRefused()
    {
        var problem = await CreateProblemAsync("Closed with nothing said");

        using var response = await host.PostAsync(
            $"/api/problems/{problem.Id}/transitions", new { targetStatus = "Closed" });
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Say what was done", body, StringComparison.Ordinal);
    }

    /// <summary>Failure path: a move the workflow does not make is a conflict, not a bad request.</summary>
    [Fact]
    public async Task Transition_ClosedToKnownError_IsAConflict()
    {
        var problem = await KnownErrorAsync("Closed then reopened as a known error");
        using var closed = await host.PostAsync($"/api/problems/{problem.Id}/transitions", new
        {
            targetStatus = "Closed",
            resolution = "Replaced the hardware.",
        });
        Assert.Equal(HttpStatusCode.OK, closed.StatusCode);

        using var response = await host.PostAsync(
            $"/api/problems/{problem.Id}/transitions", new { targetStatus = "KnownError" });
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("cannot go from Closed to KnownError", body, StringComparison.Ordinal);
    }

    /// <summary>Reopening is allowed from everywhere, and it clears the endings without erasing the record.</summary>
    [Fact]
    public async Task Transition_ClosedBackToInvestigating_ClearsTheEndingsAndKeepsTheResolutionText()
    {
        var problem = await KnownErrorAsync("Reopened problem");
        using var closed = await host.PostAsync($"/api/problems/{problem.Id}/transitions", new
        {
            targetStatus = "Closed",
            resolution = "We thought replacing the SFP had fixed it.",
        });
        Assert.Equal(HttpStatusCode.OK, closed.StatusCode);

        using var reopened = await host.PostAsync(
            $"/api/problems/{problem.Id}/transitions", new { targetStatus = "Investigating" });
        Assert.Equal(HttpStatusCode.OK, reopened.StatusCode);

        var read = await host.GetAsync<ProblemDto>($"/api/problems/{problem.Id}");
        Assert.Equal("Investigating", read.Status);
        Assert.Null(read.ClosedAt);
        Assert.Null(read.ResolvedAt);
        Assert.Equal("We thought replacing the SFP had fixed it.", read.Resolution);
    }

    /// <summary>Failure path: a service request is somebody asking for something, not a symptom.</summary>
    [Fact]
    public async Task LinkIncident_ThatIsAServiceRequest_IsRefused()
    {
        var problem = await CreateProblemAsync("Only incidents belong here");
        var request = await CreateIncidentAsync("New starter laptop", type: "ServiceRequest");

        using var response = await host.PostAsync(
            $"/api/problems/{problem.Id}/incidents", new { ticketId = request.Id });
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Only incidents", body, StringComparison.Ordinal);
    }

    /// <summary>An incident has one cause. Two problems claiming it is a triage error worth refusing.</summary>
    [Fact]
    public async Task LinkIncident_AlreadyOnAnotherProblem_IsAConflictThatNamesTheReason()
    {
        var incident = await CreateIncidentAsync("Contested incident");
        var first = await CreateProblemAsync("First claim", incidents: [incident.Id]);
        var second = await CreateProblemAsync("Second claim");

        using var response = await host.PostAsync(
            $"/api/problems/{second.Id}/incidents", new { ticketId = incident.Id });
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("already belongs to another problem", body, StringComparison.Ordinal);
        Assert.Equal(1, (await host.GetAsync<ProblemDto>($"/api/problems/{first.Id}")).IncidentCount);
    }

    [Fact]
    public async Task UnlinkIncident_RemovesItFromBothSidesAndIsAudited()
    {
        var incident = await CreateIncidentAsync("Mislinked incident");
        var problem = await CreateProblemAsync("Wrong cause", incidents: [incident.Id]);

        using var request = host.Request(HttpMethod.Delete, $"/api/problems/{problem.Id}/incidents/{incident.Id}");
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        Assert.Equal(0, (await host.GetAsync<ProblemDto>($"/api/problems/{problem.Id}")).IncidentCount);
        Assert.Empty(await host.GetAsync<List<ProblemDto>>($"/api/tickets/{incident.Id}/problems"));

        await using var scope = host.Services.CreateAsyncScope();
        var platform = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var entityId = problem.Id.ToString();
        var entries = await platform.AuditEntries
            .Where(entry => entry.EntityType == "Problem" && entry.EntityId == entityId)
            .ToListAsync();
        Assert.Contains(entries, entry => entry.Action == "Created");
        Assert.Contains(entries, entry => entry.Action == "IncidentUnlinked");
        Assert.All(entries, entry =>
            Assert.Equal(ProblemHostFixture.ProblemAuthenticationHandler.ActorId, entry.ActorId));
    }

    /// <summary>Unlinking something that was never linked is a 404, not a silent success.</summary>
    [Fact]
    public async Task UnlinkIncident_ThatWasNeverLinked_IsNotFound()
    {
        var problem = await CreateProblemAsync("Nothing linked");
        var incident = await CreateIncidentAsync("Unrelated incident");

        using var request = host.Request(HttpMethod.Delete, $"/api/problems/{problem.Id}/incidents/{incident.Id}");
        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// A problem is about a CI or about a category, never both — the state the database could hold and no
    /// screen could explain.
    /// </summary>
    [Fact]
    public async Task CreateProblem_AboutBothACiAndACategory_IsRefused()
    {
        using var response = await host.PostAsync("/api/problems", new
        {
            title = "About everything at once",
            description = "Both a CI and a category.",
            ciId = Guid.CreateVersion7(),
            categoryId = Guid.CreateVersion7(),
        });
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("not both", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateProblem_WithNoTitle_IsAValidationProblem()
    {
        using var response = await host.PostAsync("/api/problems", new { title = "", description = "Something" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    /// <summary>
    /// The closed-enum guard in its three-clause form: "3" parses and 3 is defined, so without the name
    /// comparison this would filter the board by whichever member sits at that ordinal.
    /// </summary>
    [Theory]
    [InlineData("3")]
    [InlineData("Unknown")]
    [InlineData("KnownError,3")]
    public async Task ListProblems_WithAStatusThatIsNotSpelt_IsRefused(string status)
    {
        using var request = host.Request(HttpMethod.Get, $"/api/problems?status={status}");
        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task ListProblems_ByStatus_ReturnsOnlyThatStatus()
    {
        var problem = await KnownErrorAsync($"Filterable {Guid.NewGuid():N}");

        var knownErrors = await host.GetAsync<ProblemPageDto>("/api/problems?status=KnownError&pageSize=200");
        Assert.Contains(knownErrors.Items, item => item.Id == problem.Id);
        Assert.All(knownErrors.Items, item => Assert.Equal("KnownError", item.Status));

        var investigating = await host.GetAsync<ProblemPageDto>("/api/problems?status=Investigating&pageSize=200");
        Assert.DoesNotContain(investigating.Items, item => item.Id == problem.Id);
    }

    /// <summary>
    /// A problem names causes, workarounds and other people's incidents. <c>CanManageTickets</c> includes
    /// EndUser so requesters can reach the portal, so the policy at the door cannot be the whole guard.
    /// </summary>
    [Fact]
    public async Task Problems_AsAnEndUser_AreNotReadableOrWritable()
    {
        var problem = await CreateProblemAsync("Not for requesters");

        var list = await host.GetAsync<ProblemPageDto>("/api/problems", role: "EndUser");
        Assert.Empty(list.Items);
        Assert.Equal(0, list.Total);

        using var read = host.Request(HttpMethod.Get, $"/api/problems/{problem.Id}", "EndUser");
        using var readResponse = await _client.SendAsync(read);
        Assert.Equal(HttpStatusCode.NotFound, readResponse.StatusCode);

        using var write = await host.PostAsync(
            "/api/problems", new { title = "Requester's problem", description = "No." }, "EndUser");
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
    }

    [Fact]
    public async Task GetProblem_ThatDoesNotExist_IsNotFound()
    {
        using var request = host.Request(HttpMethod.Get, $"/api/problems/{Guid.CreateVersion7()}");
        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    // ---- helpers ----

    private async Task<ProblemDto> KnownErrorAsync(string title)
    {
        var problem = await CreateProblemAsync(title);
        await UpdateAsync(problem.Id, new
        {
            title,
            description = "Recurring drops.",
            priority = "High",
            rootCause = "A failing SFP in port 23.",
            workaround = "Move the affected users to port 24.",
        });
        using var response = await host.PostAsync(
            $"/api/problems/{problem.Id}/transitions", new { targetStatus = "KnownError" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return problem;
    }

    private async Task UpdateAsync(Guid id, object body)
    {
        using var request = host.Request(HttpMethod.Put, $"/api/problems/{id}");
        request.Content = JsonContent.Create(body);
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<ProblemDto> CreateProblemAsync(string title, IReadOnlyList<Guid>? incidents = null)
    {
        using var response = await host.PostAsync("/api/problems", new
        {
            title,
            description = "Raised by the problem management integration test.",
            priority = "High",
            incidentIds = incidents,
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return Assert.IsType<ProblemDto>(await response.Content.ReadFromJsonAsync<ProblemDto>());
    }

    private async Task<TicketDto> CreateIncidentAsync(string title, string type = "Incident")
    {
        using var response = await host.PostAsync("/api/tickets", new
        {
            title,
            description = "Raised by the problem management integration test.",
            type,
            urgency = "Medium",
            impact = "Medium",
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return Assert.IsType<TicketDto>(await response.Content.ReadFromJsonAsync<TicketDto>());
    }

    private sealed record TicketDto(Guid Id, string Number, string Title, string Status);

    internal sealed record ProblemDto(
        Guid Id,
        string Number,
        string Title,
        string Description,
        string Status,
        string Priority,
        bool IsKnownError,
        SubjectDto? Subject,
        string? RootCause,
        string? Workaround,
        string? Resolution,
        string? AssignedTechnicianId,
        string OpenedById,
        string OpenedByName,
        int IncidentCount,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt,
        DateTimeOffset? KnownErrorAt,
        DateTimeOffset? ResolvedAt,
        DateTimeOffset? ClosedAt,
        List<IncidentDto>? Incidents);

    internal sealed record SubjectDto(string Scope, Guid Id, string? Name, string? Type);

    internal sealed record IncidentDto(
        Guid TicketId,
        string Number,
        string Title,
        string Status,
        string Priority,
        DateTimeOffset CreatedAt,
        string LinkedById,
        string LinkedByName,
        DateTimeOffset LinkedAt);

    internal sealed record ProblemPageDto(List<ProblemDto> Items, int Total, int Page, int PageSize);

    private sealed record TransitionDto(ProblemDto Problem, KnowledgeDraftDto? KnowledgeDraft);

    private sealed record KnowledgeDraftDto(
        Guid ProblemId,
        string ProblemNumber,
        string Title,
        string? SubjectName,
        List<SymptomDto> Symptoms,
        string? RootCause,
        string? Workaround,
        string? Resolution,
        List<string> IncidentNumbers);

    private sealed record SymptomDto(string Text, int IncidentCount);
}
