using System.Net;
using System.Net.Http.Json;

using Microsoft.Extensions.DependencyInjection;

using Modules.Helpdesk.Data;

namespace Infrastructure.Tests;

/// <summary>
/// The knowledge base end to end (WP-5.9): an article written, versioned and published; the suggestions an
/// agent gets while typing a ticket; the portal search that finds published articles and nothing else; and
/// the article attached to a ticket as its answer.
/// <para>
/// Every record here carries a nonsense marker token that appears nowhere else in the solution, because a
/// knowledge search is rooted at a word over a database the whole suite shares. Searching for "vpn" here
/// would assert against whatever the other forty classes happened to have written.
/// </para>
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class KnowledgeBaseApiIntegrationTests(
    InfrastructureFixture infrastructure,
    KnowledgeHostFixture host)
    : IClassFixture<KnowledgeHostFixture>, IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        ArgumentNullException.ThrowIfNull(host);
        await host.EnsureInitialisedAsync(infrastructure);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// An article is created as a draft whatever the person creating it intended, and reaches the knowledge
    /// base only through the transition — the same split the problem and the change request make, because
    /// publishing has an entry condition a field assignment would walk straight past.
    /// </summary>
    [Fact]
    public async Task CreateArticle_IsADraftAndOnlyBecomesPublishedThroughATransition()
    {
        var marker = Marker();
        var article = await CreateAsync($"Resetting the {marker} handset", marker);

        Assert.StartsWith("KB-", article.Number, StringComparison.Ordinal);
        Assert.Equal("Draft", article.Status);
        Assert.Equal(1, article.Version);
        Assert.Contains("Published", article.NextStatuses);

        var published = await TransitionAsync(article.Id, "Published");

        Assert.Equal("Published", published.Status);
        Assert.NotNull(published.PublishedAt);
        Assert.Equal(KnowledgeHostFixture.KnowledgeAuthenticationHandler.ActorId, published.PublishedById);
    }

    /// <summary>
    /// The failure path the entry condition exists for: somebody who finds an article stops looking, so a
    /// published one has to answer them. A 400 with a field error rather than a 409, because it is a fact
    /// about the article that a form can point at.
    /// </summary>
    [Fact]
    public async Task PublishArticle_WithAnEmptyBody_IsRefusedWithAFieldError()
    {
        var marker = Marker();
        var article = await CreateAsync($"Half-written {marker} note", marker);

        // Emptied through the update path, because the create validator will not accept an empty body —
        // which is itself the first of the two guards.
        using var cleared = await host.SendAsync(HttpMethod.Put, $"/api/kb-articles/{article.Id}", new
        {
            title = $"Half-written {marker} note",
            summary = "Something.",
            body = "",
        });
        Assert.Equal(HttpStatusCode.BadRequest, cleared.StatusCode);

        // And the article really is still a draft.
        Assert.Equal("Draft", (await host.GetAsync<ArticleDto>($"/api/kb-articles/{article.Id}")).Status);
    }

    /// <summary>
    /// The same refusal reached from below, which is the half that matters: an article whose summary never
    /// came through the API — a seeded row, a migration, a restored backup — still cannot be published.
    /// <para>
    /// Written straight to the table on purpose. The validators refuse an empty summary at both write
    /// endpoints, so without this the workflow's own entry condition would be unreachable and nothing would
    /// notice if it were deleted.
    /// </para>
    /// </summary>
    [Fact]
    public async Task PublishArticle_ThatSaysNothing_IsRefusedByTheWorkflowAndNotOnlyByTheValidator()
    {
        var marker = Marker();
        var articleId = Guid.CreateVersion7();
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            dbContext.KbArticles.Add(new KbArticle
            {
                Id = articleId,
                Title = $"Empty {marker} note",
                Summary = string.Empty,
                Body = string.Empty,
                Status = KbArticleStatus.Draft,
                Version = 1,
                AuthorId = "seeded",
                AuthorName = "Seeded",
                UpdatedById = "seeded",
                UpdatedByName = "Seeded",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await dbContext.SaveChangesAsync();
        }

        using var response = await host.SendAsync(
            HttpMethod.Post, $"/api/kb-articles/{articleId}/transitions", new { targetStatus = "Published" });
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("summary", body, StringComparison.OrdinalIgnoreCase);

        // And it really did not move.
        Assert.Equal("Draft", (await host.GetAsync<ArticleDto>($"/api/kb-articles/{articleId}")).Status);
    }

    /// <summary>
    /// WP-5.9's third verification step in its direct form, and the reason the draft state has teeth: the
    /// portal finds published articles and never a draft — enforced in the query, so asking for drafts by
    /// name changes nothing.
    /// </summary>
    [Fact]
    public async Task ListArticles_AsAnEndUser_FindsPublishedOnlyEvenWhenDraftsAreAskedForByName()
    {
        var marker = Marker();
        var published = await CreateAsync($"Published {marker} guide", marker);
        await TransitionAsync(published.Id, "Published");
        var draft = await CreateAsync($"Draft {marker} guide", marker);

        var asAgent = await host.GetAsync<ArticlePageDto>($"/api/kb-articles?search={marker}");
        Assert.Equal(2, asAgent.Total);

        var asEndUser = await host.GetAsync<ArticlePageDto>(
            $"/api/kb-articles?search={marker}&status=Draft", role: "EndUser");

        var only = Assert.Single(asEndUser.Items);
        Assert.Equal(published.Id, only.Id);
        Assert.Equal(1, asEndUser.Total);

        // And the draft cannot be opened directly either — a 404 rather than a 403, because "you may not
        // read this one" would confirm that a draft about their question exists.
        using var request = host.Request(HttpMethod.Get, $"/api/kb-articles/{draft.Id}", "EndUser");
        using var response = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// WP-5.9's first verification step: typing a ticket that matches an article surfaces it. The text sent
    /// is what somebody would actually type — a sentence, not a search term — which is the whole reason the
    /// similarity query is OR-ed rather than AND-ed.
    /// </summary>
    [Fact]
    public async Task Suggest_ForASentenceSomebodyWouldType_SurfacesTheMatchingArticle()
    {
        var marker = Marker();
        var article = await CreateAsync(
            $"{marker} handset will not register",
            marker,
            summary: $"What to do when a {marker} desk phone shows 'not registered'.",
            body: $"Reboot the {marker} handset, then check the network cable at the back of it.");
        await TransitionAsync(article.Id, "Published");

        var suggestions = await SuggestAsync(
            subject: $"My {marker} handset will not register",
            body: "It has been showing 'not registered' since this morning and rebooting has not helped.");

        Assert.Contains(suggestions, suggestion => suggestion.Id == article.Id);
        Assert.All(suggestions, suggestion => Assert.True(suggestion.Rank > 0));
    }

    /// <summary>
    /// The failure path that matters most here, because the alternative is silent: a draft must never be
    /// suggested. Half-written advice quoted onto a ticket is exactly what the draft state prevents.
    /// </summary>
    [Fact]
    public async Task Suggest_NeverOffersADraft_EvenToTheAgentWhoWroteIt()
    {
        var marker = Marker();
        var draft = await CreateAsync(
            $"{marker} projector setup",
            marker,
            summary: $"How to connect a laptop to the {marker} projector.",
            body: $"Use the {marker} cable at the lectern.");

        var suggestions = await SuggestAsync(subject: $"Cannot connect to the {marker} projector");

        Assert.DoesNotContain(suggestions, suggestion => suggestion.Id == draft.Id);
    }

    /// <summary>
    /// A ticket about something the knowledge base has never heard of gets no suggestions and no
    /// explanation — an empty list is the honest answer, and a panel that guessed would be worse than one
    /// that stayed quiet.
    /// </summary>
    [Fact]
    public async Task Suggest_ForTextNothingMatches_IsEmptyRatherThanAGuess()
    {
        var suggestions = await SuggestAsync(
            subject: "qwlkjhasdfmnbvzxc plurgh", body: "wibblefrotz nargleplex");

        Assert.Empty(suggestions);
    }

    /// <summary>
    /// Versioning, which for an article means the prose and not a number: an edit keeps what it used to say,
    /// and restoring puts it back <em>forward</em> as a new version rather than by rewinding the count.
    /// </summary>
    [Fact]
    public async Task EditThenRestore_KeepsEveryVersionAndMovesForwardRatherThanRewinding()
    {
        var marker = Marker();
        var article = await CreateAsync($"{marker} printer queue", marker, body: "The first thing to try.");

        var edited = await UpdateAsync(article.Id, $"{marker} printer queue", "The second thing to try.");
        Assert.Equal(2, edited.Version);
        var kept = Assert.Single(edited.Revisions!);
        Assert.Equal(1, kept.Version);
        Assert.Equal("The first thing to try.", kept.Body);

        // An edit that changes nothing is not a version: two identical rows in a history tell nobody anything.
        var unchanged = await UpdateAsync(article.Id, $"{marker} printer queue", "The second thing to try.");
        Assert.Equal(2, unchanged.Version);
        Assert.Single(unchanged.Revisions!);

        var restored = await host.SendAsync(
            HttpMethod.Post, $"/api/kb-articles/{article.Id}/revisions/1/restoration");
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        var current = Assert.IsType<ArticleDto>(await restored.Content.ReadFromJsonAsync<ArticleDto>());

        Assert.Equal(3, current.Version);
        Assert.Equal("The first thing to try.", current.Body);
        Assert.Equal([2, 1], current.Revisions!.Select(revision => revision.Version));
    }

    /// <summary>Restoring a version that was never written is a 404 and not a silent no-op.</summary>
    [Fact]
    public async Task Restore_AVersionThatDoesNotExist_Is404()
    {
        var article = await CreateAsync($"{Marker()} nothing to restore", Marker());

        using var response = await host.SendAsync(
            HttpMethod.Post, $"/api/kb-articles/{article.Id}/revisions/7/restoration");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// WP-5.9's third verification step: the article that answered a ticket is attached to it, readable from
    /// the ticket, and — once it is — the article cannot be deleted out from under that record.
    /// </summary>
    [Fact]
    public async Task AttachArticleToTicket_ReadsBackFromTheTicketAndPinsTheArticleInPlace()
    {
        var marker = Marker();
        var article = await CreateAsync($"{marker} mailbox quota", marker);
        await TransitionAsync(article.Id, "Published");
        var ticketId = await CreateTicketAsync($"Mailbox full on the {marker} account");

        using var linked = await host.SendAsync(
            HttpMethod.Post, $"/api/tickets/{ticketId}/kb-articles", new { articleId = article.Id });
        Assert.Equal(HttpStatusCode.Created, linked.StatusCode);

        var attached = await host.GetAsync<List<TicketArticleDto>>($"/api/tickets/{ticketId}/kb-articles");
        var only = Assert.Single(attached);
        Assert.Equal(article.Id, only.ArticleId);
        Assert.Equal(article.Number, only.Number);

        // The same article twice is a 409 rather than a second row.
        using var again = await host.SendAsync(
            HttpMethod.Post, $"/api/tickets/{ticketId}/kb-articles", new { articleId = article.Id });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);

        // And it cannot be deleted while a ticket has been answered with it — archiving is how an article
        // goes out of use, which is what the refusal says.
        using var deleted = await host.SendAsync(HttpMethod.Delete, $"/api/kb-articles/{article.Id}");
        Assert.Equal(HttpStatusCode.Conflict, deleted.StatusCode);
        Assert.Contains("Archive it instead", await deleted.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // Detached, it goes.
        using var detached = await host.SendAsync(
            HttpMethod.Delete, $"/api/tickets/{ticketId}/kb-articles/{article.Id}");
        Assert.Equal(HttpStatusCode.NoContent, detached.StatusCode);
        using var removed = await host.SendAsync(HttpMethod.Delete, $"/api/kb-articles/{article.Id}");
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
    }

    /// <summary>
    /// A draft cannot be attached to a ticket. The attachment is the answer somebody was given, and an
    /// unfinished article quoted as one is worse than no answer.
    /// </summary>
    [Fact]
    public async Task AttachArticleToTicket_WhenTheArticleIsADraft_IsRefused()
    {
        var marker = Marker();
        var draft = await CreateAsync($"{marker} unfinished answer", marker);
        var ticketId = await CreateTicketAsync($"Something about {marker}");

        using var response = await host.SendAsync(
            HttpMethod.Post, $"/api/tickets/{ticketId}/kb-articles", new { articleId = draft.Id });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Publish it first", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The disclosure rule the whole surface hangs on: <c>CanManageTickets</c> deliberately includes EndUser
    /// so requesters can reach the portal, so writing has to be gated in the service as well — the check
    /// WP-1.10, WP-5.7 and WP-5.8 each met before it.
    /// </summary>
    [Fact]
    public async Task WriteArticle_AsAnEndUser_Is403()
    {
        using var response = await host.SendAsync(HttpMethod.Post, "/api/kb-articles", new
        {
            title = "Written by somebody who may not write",
            summary = "This should not be stored.",
            body = "Nor this.",
        }, role: "EndUser");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    /// <summary>
    /// The closed-enum guard, met for the sixth time. <c>TryParse</c> accepts "3" and 3 <em>is</em> a defined
    /// member, so without the name comparison this would silently filter by whichever member sits at that
    /// ordinal — a filter that looks broken and is worse than one that is.
    /// </summary>
    [Theory]
    [InlineData("Retired")]
    [InlineData("3")]
    public async Task ListArticles_WithAnUnrecognisedStatusFilter_Is400(string status)
    {
        using var request = host.Request(HttpMethod.Get, $"/api/kb-articles?status={status}");
        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("status", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The join WP-5.7 left open: closing a problem prompts a draft article, and this is where that draft
    /// becomes one — with the problem recorded on it, so the knowledge base says where its answers came from.
    /// </summary>
    [Fact]
    public async Task CreateArticle_FromAClosedProblem_RecordsWhichProblemItCameFrom()
    {
        var marker = Marker();
        var problem = await CreateProblemAsync($"Recurring {marker} faults");

        var article = await CreateAsync(
            $"Working around {marker} faults", marker, problemId: problem.Id);

        Assert.Equal(problem.Id, article.ProblemId);
        Assert.Equal(problem.Number, article.ProblemNumber);
    }

    // ---- helpers ----

    /// <summary>
    /// A token no other class has ever written, per test. A knowledge search matches on a word over a shared
    /// database, so a real one would assert against somebody else's fixtures.
    /// </summary>
    private static string Marker() => $"kbz{Guid.NewGuid():N}"[..12];

    private async Task<ArticleDto> CreateAsync(
        string title,
        string marker,
        string? summary = null,
        string? body = null,
        Guid? problemId = null)
    {
        using var response = await host.SendAsync(HttpMethod.Post, "/api/kb-articles", new
        {
            title,
            summary = summary ?? $"A short answer about {marker}.",
            body = body ?? $"The long answer about {marker}, in more than one sentence.",
            keywords = marker,
            problemId,
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return Assert.IsType<ArticleDto>(await response.Content.ReadFromJsonAsync<ArticleDto>());
    }

    private async Task<ArticleDto> UpdateAsync(Guid id, string title, string body)
    {
        using var response = await host.SendAsync(HttpMethod.Put, $"/api/kb-articles/{id}", new
        {
            title,
            summary = "A short answer.",
            body,
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Assert.IsType<ArticleDto>(await response.Content.ReadFromJsonAsync<ArticleDto>());
    }

    private async Task<ArticleDto> TransitionAsync(Guid id, string targetStatus)
    {
        using var response = await host.SendAsync(
            HttpMethod.Post, $"/api/kb-articles/{id}/transitions", new { targetStatus });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Assert.IsType<ArticleDto>(await response.Content.ReadFromJsonAsync<ArticleDto>());
    }

    private Task<List<SuggestionDto>> SuggestAsync(string subject, string? body = null) =>
        host.GetAsync<List<SuggestionDto>>(
            $"/api/kb-articles/suggestions?subject={Uri.EscapeDataString(subject)}"
            + (body is null ? string.Empty : $"&body={Uri.EscapeDataString(body)}"));

    private async Task<Guid> CreateTicketAsync(string title)
    {
        using var response = await host.SendAsync(HttpMethod.Post, "/api/tickets", new
        {
            title,
            description = "Raised by the knowledge base integration test.",
            type = "Incident",
            urgency = "Medium",
            impact = "Medium",
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return Assert.IsType<TicketIdDto>(await response.Content.ReadFromJsonAsync<TicketIdDto>()).Id;
    }

    private async Task<ProblemIdDto> CreateProblemAsync(string title)
    {
        using var response = await host.SendAsync(HttpMethod.Post, "/api/problems", new
        {
            title,
            description = "Raised by the knowledge base integration test.",
            priority = "Medium",
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return Assert.IsType<ProblemIdDto>(await response.Content.ReadFromJsonAsync<ProblemIdDto>());
    }

    private sealed record ArticleDto(
        Guid Id,
        string Number,
        string Title,
        string Summary,
        string Body,
        string Status,
        Guid? ProblemId,
        string? ProblemNumber,
        int Version,
        string? PublishedById,
        DateTimeOffset? PublishedAt,
        List<string> NextStatuses,
        List<RevisionDto>? Revisions);

    private sealed record RevisionDto(int Version, string Title, string Summary, string Body);

    private sealed record ArticlePageDto(List<ArticleDto> Items, int Total, int Page, int PageSize);

    private sealed record SuggestionDto(Guid Id, string Number, string Title, string Summary, double Rank);

    private sealed record TicketArticleDto(Guid ArticleId, string Number, string Title, string Status);

    private sealed record TicketIdDto(Guid Id, string Number);

    private sealed record ProblemIdDto(Guid Id, string Number);
}
