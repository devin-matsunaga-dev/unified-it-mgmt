using System.Security.Claims;

using Microsoft.EntityFrameworkCore;

using Modules.Helpdesk.Data;
using Platform.Actors;
using Platform.Auditing;
using Platform.Search;

namespace Modules.Helpdesk.Features.Knowledge;

public sealed class KbArticleService(
    HelpdeskDbContext dbContext,
    IAuditService auditService) : IKbArticleService
{
    private const int MaximumPageSize = 200;

    /// <summary>How many suggestions a caller can ask for. Five is the default; a screen shows a handful.</summary>
    private const int MaximumSuggestions = 20;

    /// <summary>Revisions returned beside one article. Beyond this the panel is a list nobody scrolls.</summary>
    private const int MaximumRevisionsShown = 50;

    public async Task<KbArticlePageResponse> ListAsync(
        KbArticleListFilter filter,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var page = Math.Max(filter.Page, 1);
        var pageSize = Math.Clamp(filter.PageSize, 1, MaximumPageSize);
        var isAgent = ActorRoles.IsAgent(actor);

        var query = dbContext.KbArticles.AsNoTracking().AsQueryable();

        // The portal rule, applied in the query and never by hiding a control: CanManageTickets deliberately
        // includes EndUser so requesters can reach the portal, so a status filter an end user sends is
        // answered with published articles regardless of what it asked for (WP-1.8).
        if (!isAgent)
        {
            query = query.Where(article => KbArticleStatuses.Readable.Contains(article.Status));
        }
        else if (filter.Statuses is { Count: > 0 } statuses)
        {
            // Contains rather than a range: every enum here is stored as text and a comparison would be a
            // comparison of words (WP-5.6).
            var wanted = statuses.Distinct().ToArray();
            query = query.Where(article => wanted.Contains(article.Status));
        }

        if (filter.CategoryId is { } categoryId)
        {
            query = query.Where(article => article.CategoryId == categoryId);
        }

        var tsQuery = SearchTerm.ToPrefixTsQuery(filter.Search);
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            // A term holding nothing searchable — punctuation, an emoji — matches nothing rather than
            // everything. The alternative is a search box that answers a question nobody asked with the
            // whole knowledge base (WP-5.4's reasoning, expressed here as a filter rather than a 400
            // because this is a list endpoint and its other filters still apply).
            if (tsQuery is null)
            {
                query = query.Where(_ => false);
            }
            else
            {
                var sequenceNumber = ToSequenceNumber(filter.Search);
                query = query.Where(article =>
                    article.SearchVector.Matches(EF.Functions.ToTsQuery(SearchTerm.Configuration, tsQuery))
                    || (sequenceNumber != null && article.SequenceNumber == sequenceNumber));
            }
        }

        var total = await query.CountAsync(cancellationToken);

        // Ranked while somebody is searching, newest-first while they are browsing. Ordering a browse by
        // rank would be ordering it by a number that is the same for every row.
        var ordered = tsQuery is null
            ? query.OrderByDescending(article => article.UpdatedAt).ThenBy(article => article.Id)
            : query
                .OrderByDescending(article =>
                    article.SearchVector.Rank(EF.Functions.ToTsQuery(SearchTerm.Configuration, tsQuery)))
                .ThenByDescending(article => article.UpdatedAt)
                .ThenBy(article => article.Id);

        var articles = await ordered
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        var context = await ContextAsync(articles, cancellationToken);
        return new KbArticlePageResponse(
            [.. articles.Select(article => Map(article, context))],
            total,
            page,
            pageSize);
    }

    public async Task<KbArticleResponse?> GetAsync(Guid id, ClaimsPrincipal actor, CancellationToken cancellationToken)
    {
        var article = await dbContext.KbArticles.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (article is null)
        {
            return null;
        }

        // A draft an end user asked for is a 404 and not a 403: "you may not read this one" tells them a
        // draft about their question exists, which is the disclosure the draft state exists to prevent.
        if (!ActorRoles.IsAgent(actor) && !KbArticleStatuses.Readable.Contains(article.Status))
        {
            return null;
        }

        var context = await ContextAsync([article], cancellationToken);
        var revisions = ActorRoles.IsAgent(actor)
            ? await RevisionsAsync(id, cancellationToken)
            : null;
        return Map(article, context, revisions);
    }

    public async Task<KbArticleResult> CreateAsync(
        CreateKbArticleRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!ActorRoles.IsAgent(actor))
        {
            return new(KbOutcome.Forbidden, Error: "Writing knowledge is an agent surface.");
        }

        if (await ReferenceErrorsAsync(request.CategoryId, request.ProblemId, cancellationToken) is { } errors)
        {
            return new(KbOutcome.Invalid, Errors: errors);
        }

        var now = DateTimeOffset.UtcNow;
        var article = new KbArticle
        {
            Id = Guid.CreateVersion7(),
            Title = request.Title.Trim(),
            Summary = request.Summary.Trim(),
            Body = request.Body.Trim(),
            Keywords = Trimmed(request.Keywords),
            // Always a draft. Publishing is an act with an entry condition, so it goes through the
            // transition endpoint even when the person creating it means to publish immediately.
            Status = KbArticleStatus.Draft,
            CategoryId = request.CategoryId,
            ProblemId = request.ProblemId,
            Version = 1,
            AuthorId = ActorId(actor),
            AuthorName = ActorDisplayName(actor),
            UpdatedById = ActorId(actor),
            UpdatedByName = ActorDisplayName(actor),
            CreatedAt = now,
            UpdatedAt = now,
        };
        dbContext.KbArticles.Add(article);
        await dbContext.SaveChangesAsync(cancellationToken);

        var response = await ReadBackAsync(article.Id, cancellationToken);
        await auditService.WriteAsync(
            actor, "Created", "KbArticle", article.Id.ToString(), null, response, cancellationToken);
        return new(KbOutcome.Success, response);
    }

    public async Task<KbArticleResult> UpdateAsync(
        Guid id,
        UpdateKbArticleRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!ActorRoles.IsAgent(actor))
        {
            return new(KbOutcome.Forbidden, Error: "Writing knowledge is an agent surface.");
        }

        var article = await dbContext.KbArticles.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (article is null)
        {
            return new(KbOutcome.NotFound);
        }

        if (await ReferenceErrorsAsync(request.CategoryId, problemId: null, cancellationToken) is { } errors)
        {
            return new(KbOutcome.Invalid, Errors: errors);
        }

        var before = await ReadBackAsync(id, cancellationToken);
        var changed = Apply(
            article,
            request.Title.Trim(),
            request.Summary.Trim(),
            request.Body.Trim(),
            Trimmed(request.Keywords),
            request.CategoryId,
            actor);
        await dbContext.SaveChangesAsync(cancellationToken);

        var after = await ReadBackAsync(id, cancellationToken);
        // An edit that changed nothing is still an edit somebody made, but it is not a version: recording
        // it as one would fill the history with rows that differ from their neighbour in no respect.
        await auditService.WriteAsync(
            actor, changed ? "Updated" : "UpdatedWithoutChange", "KbArticle", id.ToString(), before, after, cancellationToken);
        return new(KbOutcome.Success, after);
    }

    public async Task<KbArticleResult> TransitionAsync(
        Guid id,
        KbTransitionRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!ActorRoles.IsAgent(actor))
        {
            return new(KbOutcome.Forbidden, Error: "Writing knowledge is an agent surface.");
        }

        var article = await dbContext.KbArticles.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (article is null)
        {
            return new(KbOutcome.NotFound);
        }

        var verdict = KbWorkflow.Check(article, request.TargetStatus);
        if (verdict != KbTransitionVerdict.Allowed)
        {
            var explanation = KbWorkflow.Explain(article.Status, request.TargetStatus, verdict);
            // A missing summary or body is a fact about the article that a form can point at; a move the
            // workflow does not make is a fact about the state it is in.
            return verdict == KbTransitionVerdict.NeedsContent
                ? new(KbOutcome.Invalid, Errors: Field(nameof(UpdateKbArticleRequest.Body), explanation))
                : new(KbOutcome.InvalidTransition, Error: explanation);
        }

        var before = await ReadBackAsync(id, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        article.Status = request.TargetStatus;
        article.UpdatedAt = now;
        switch (request.TargetStatus)
        {
            case KbArticleStatus.Published:
                article.PublishedAt = now;
                article.PublishedById = ActorId(actor);
                article.PublishedByName = ActorDisplayName(actor);
                article.ArchivedAt = null;
                break;
            case KbArticleStatus.Archived:
                article.ArchivedAt = now;
                break;
            case KbArticleStatus.Draft:
                // Nothing is cleared. PublishedAt stays as the record of when this was last live, which is
                // what somebody looking at a withdrawn article asks first.
                article.ArchivedAt = null;
                break;
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        var after = await ReadBackAsync(id, cancellationToken);
        await auditService.WriteAsync(
            actor, $"TransitionedTo{request.TargetStatus}", "KbArticle", id.ToString(), before, after, cancellationToken);
        return new(KbOutcome.Success, after);
    }

    public async Task<KbArticleResult> RestoreAsync(
        Guid id,
        int version,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        if (!ActorRoles.IsAgent(actor))
        {
            return new(KbOutcome.Forbidden, Error: "Writing knowledge is an agent surface.");
        }

        var article = await dbContext.KbArticles.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (article is null)
        {
            return new(KbOutcome.NotFound);
        }

        var revision = await dbContext.KbArticleRevisions.AsNoTracking()
            .SingleOrDefaultAsync(item => item.ArticleId == id && item.Version == version, cancellationToken);
        if (revision is null)
        {
            return new(KbOutcome.NotFound, Error: $"This article has no version {version}.");
        }

        var before = await ReadBackAsync(id, cancellationToken);
        // Forward as a new version rather than by rewinding the number. The history is a record of what was
        // published and when, so using it must not be a way to edit it.
        var changed = Apply(
            article,
            revision.Title,
            revision.Summary,
            revision.Body,
            revision.Keywords,
            revision.CategoryId,
            actor);
        if (!changed)
        {
            return new(
                KbOutcome.InvalidTransition,
                Error: $"Version {version} is what this article already says.");
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        var after = await ReadBackAsync(id, cancellationToken);
        await auditService.WriteAsync(
            actor, $"RestoredVersion{version}", "KbArticle", id.ToString(), before, after, cancellationToken);
        return new(KbOutcome.Success, after);
    }

    public async Task<IReadOnlyList<KbSuggestionResponse>> SuggestAsync(
        KbSuggestionRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The subject twice, deliberately. A suggestion is asked while a ticket is being typed, and the one
        // line somebody has written first carries most of the meaning — repeating it doubles its term
        // frequency against a body that may be four paragraphs of log output.
        var text = string.Join(
            ' ',
            new[] { request.Subject, request.Subject, request.Body }
                .Where(part => !string.IsNullOrWhiteSpace(part)));
        var tsQuery = SearchTerm.ToSimilarityTsQuery(text);
        if (tsQuery is null)
        {
            return [];
        }

        var limit = Math.Clamp(request.Limit, 1, MaximumSuggestions);

        // Published only, for everybody including agents. A draft surfacing here would put half-written
        // advice on a ticket, which is precisely what the draft state exists to prevent — and the same
        // query then serves the portal's deflection prompt, so there is one rule rather than two.
        var query = dbContext.KbArticles.AsNoTracking()
            .Where(article => article.Status == KbArticleStatus.Published)
            .Where(article => article.SearchVector.Matches(
                EF.Functions.ToTsQuery(SearchTerm.Configuration, tsQuery)));

        var rows = await query
            .Select(article => new
            {
                article.Id,
                article.SequenceNumber,
                article.Title,
                article.Summary,
                article.CategoryId,
                CategoryName = article.Category!.Name,
                article.PublishedAt,
                Rank = article.SearchVector.Rank(EF.Functions.ToTsQuery(SearchTerm.Configuration, tsQuery)),
                // The category the form already has selected wins ties and near-ties. It is the one thing
                // the platform knows about the ticket that the prose does not say.
                SameCategory = request.CategoryId != null && article.CategoryId == request.CategoryId,
            })
            .OrderByDescending(row => row.SameCategory)
            .ThenByDescending(row => row.Rank)
            .ThenByDescending(row => row.PublishedAt)
            .Take(limit)
            .ToListAsync(cancellationToken);

        return [.. rows.Select(row => new KbSuggestionResponse(
            row.Id,
            $"KB-{row.SequenceNumber:000000}",
            row.Title,
            row.Summary,
            row.CategoryName,
            row.PublishedAt,
            row.Rank))];
    }

    public async Task<IReadOnlyList<TicketKbArticleResponse>> ListForTicketAsync(
        Guid ticketId,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        if (!ActorRoles.IsAgent(actor))
        {
            return [];
        }

        // Flat columns rather than Include-then-Select: EF ignores an Include the moment a query projects a
        // shape other than the entity it started from, and the navigation would arrive null (WP-5.5).
        var rows = await dbContext.TicketKbArticles.AsNoTracking()
            .Where(link => link.TicketId == ticketId)
            .OrderBy(link => link.LinkedAt)
            .Select(link => new
            {
                link.ArticleId,
                link.Article.SequenceNumber,
                link.Article.Title,
                link.Article.Summary,
                link.Article.Status,
                link.LinkedById,
                link.LinkedByName,
                link.LinkedAt,
            })
            .ToListAsync(cancellationToken);

        return [.. rows.Select(row => new TicketKbArticleResponse(
            row.ArticleId,
            $"KB-{row.SequenceNumber:000000}",
            row.Title,
            row.Summary,
            row.Status,
            row.LinkedById,
            row.LinkedByName,
            row.LinkedAt))];
    }

    public async Task<TicketKbArticleResult> LinkToTicketAsync(
        Guid ticketId,
        Guid articleId,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        if (!ActorRoles.IsAgent(actor))
        {
            return new(KbOutcome.Forbidden, Error: "Attaching an article is an agent surface.");
        }

        if (!await dbContext.Tickets.AnyAsync(ticket => ticket.Id == ticketId, cancellationToken))
        {
            return new(KbOutcome.NotFound, Error: "That ticket does not exist.");
        }

        var article = await dbContext.KbArticles.AsNoTracking()
            .Where(item => item.Id == articleId)
            .Select(item => new { item.Id, item.Status })
            .SingleOrDefaultAsync(cancellationToken);
        if (article is null)
        {
            return new(KbOutcome.Invalid, Errors: Field(
                nameof(LinkKbArticleRequest.ArticleId), "That article does not exist."));
        }

        // Published only. An attached article is the answer this ticket was resolved with, and half-written
        // advice quoted as a resolution is worse than no answer at all.
        if (article.Status != KbArticleStatus.Published)
        {
            return new(KbOutcome.Invalid, Errors: Field(
                nameof(LinkKbArticleRequest.ArticleId),
                "Only a published article can be attached to a ticket. Publish it first."));
        }

        if (await dbContext.TicketKbArticles
            .AnyAsync(link => link.TicketId == ticketId && link.ArticleId == articleId, cancellationToken))
        {
            return new(KbOutcome.Duplicate, Error: "That article is already attached to this ticket.");
        }

        var now = DateTimeOffset.UtcNow;
        dbContext.TicketKbArticles.Add(new TicketKbArticle
        {
            Id = Guid.CreateVersion7(),
            TicketId = ticketId,
            ArticleId = articleId,
            LinkedById = ActorId(actor),
            LinkedByName = ActorDisplayName(actor),
            LinkedAt = now,
        });
        await dbContext.SaveChangesAsync(cancellationToken);

        await auditService.WriteAsync(
            actor, "ArticleAttached", "Ticket", ticketId.ToString(), null, new { ArticleId = articleId }, cancellationToken);
        var links = await ListForTicketAsync(ticketId, actor, cancellationToken);
        return new(KbOutcome.Success, links.Single(link => link.ArticleId == articleId));
    }

    public async Task<KbOutcome> UnlinkFromTicketAsync(
        Guid ticketId,
        Guid articleId,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        if (!ActorRoles.IsAgent(actor))
        {
            return KbOutcome.Forbidden;
        }

        var link = await dbContext.TicketKbArticles
            .SingleOrDefaultAsync(item => item.TicketId == ticketId && item.ArticleId == articleId, cancellationToken);
        if (link is null)
        {
            return KbOutcome.NotFound;
        }

        dbContext.TicketKbArticles.Remove(link);
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditService.WriteAsync(
            actor, "ArticleDetached", "Ticket", ticketId.ToString(), new { ArticleId = articleId }, null, cancellationToken);
        return KbOutcome.Success;
    }

    /// <summary>
    /// Deletes an article that nothing points at. Refused rather than cascaded once a ticket has been
    /// answered with it, following WP-5.6's runbook rule: those links are the record of what somebody was
    /// told, and archiving is the way an article goes out of use.
    /// </summary>
    public async Task<KbOutcome> DeleteAsync(Guid id, ClaimsPrincipal actor, CancellationToken cancellationToken)
    {
        if (!ActorRoles.IsAgent(actor))
        {
            return KbOutcome.Forbidden;
        }

        var article = await dbContext.KbArticles.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (article is null)
        {
            return KbOutcome.NotFound;
        }

        if (await dbContext.TicketKbArticles.AnyAsync(link => link.ArticleId == id, cancellationToken))
        {
            return KbOutcome.InvalidTransition;
        }

        var before = await ReadBackAsync(id, cancellationToken);
        dbContext.KbArticles.Remove(article);
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditService.WriteAsync(
            actor, "Deleted", "KbArticle", id.ToString(), before, null, cancellationToken);
        return KbOutcome.Success;
    }

    // ---- internals ----

    /// <summary>
    /// Writes the content onto the article, keeping what it said as a revision first. Returns whether
    /// anything actually changed, so a save that changed nothing does not manufacture a version.
    /// </summary>
    private bool Apply(
        KbArticle article,
        string title,
        string summary,
        string body,
        string? keywords,
        Guid? categoryId,
        ClaimsPrincipal actor)
    {
        var unchanged = article.Title == title
            && article.Summary == summary
            && article.Body == body
            && article.Keywords == keywords
            && article.CategoryId == categoryId;
        if (unchanged)
        {
            return false;
        }

        dbContext.KbArticleRevisions.Add(new KbArticleRevision
        {
            Id = Guid.CreateVersion7(),
            ArticleId = article.Id,
            Version = article.Version,
            Title = article.Title,
            Summary = article.Summary,
            Body = article.Body,
            Keywords = article.Keywords,
            CategoryId = article.CategoryId,
            AuthorId = article.UpdatedById,
            AuthorName = article.UpdatedByName,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        article.Title = title;
        article.Summary = summary;
        article.Body = body;
        article.Keywords = keywords;
        article.CategoryId = categoryId;
        article.Version += 1;
        article.UpdatedById = ActorId(actor);
        article.UpdatedByName = ActorDisplayName(actor);
        article.UpdatedAt = DateTimeOffset.UtcNow;
        return true;
    }

    private async Task<KbArticleResponse> ReadBackAsync(Guid id, CancellationToken cancellationToken)
    {
        var article = await dbContext.KbArticles.AsNoTracking().SingleAsync(item => item.Id == id, cancellationToken);
        var context = await ContextAsync([article], cancellationToken);
        return Map(article, context, await RevisionsAsync(id, cancellationToken));
    }

    private async Task<IReadOnlyList<KbRevisionResponse>> RevisionsAsync(
        Guid articleId,
        CancellationToken cancellationToken)
    {
        var rows = await dbContext.KbArticleRevisions.AsNoTracking()
            .Where(revision => revision.ArticleId == articleId)
            .OrderByDescending(revision => revision.Version)
            .Take(MaximumRevisionsShown)
            .ToListAsync(cancellationToken);
        return [.. rows.Select(revision => new KbRevisionResponse(
            revision.Version,
            revision.Title,
            revision.Summary,
            revision.Body,
            revision.Keywords,
            revision.AuthorId,
            revision.AuthorName,
            revision.CreatedAt))];
    }

    /// <summary>
    /// Names and counts for a page of articles, read once each rather than per row — a list of twenty-five
    /// must not be fifty round trips.
    /// </summary>
    private async Task<KbArticleContext> ContextAsync(
        IReadOnlyCollection<KbArticle> articles,
        CancellationToken cancellationToken)
    {
        if (articles.Count == 0)
        {
            return new KbArticleContext(
                new Dictionary<Guid, string>(),
                new Dictionary<Guid, string>(),
                new Dictionary<Guid, int>());
        }

        var categoryIds = articles.Where(article => article.CategoryId is not null)
            .Select(article => article.CategoryId!.Value).Distinct().ToArray();
        var problemIds = articles.Where(article => article.ProblemId is not null)
            .Select(article => article.ProblemId!.Value).Distinct().ToArray();
        var articleIds = articles.Select(article => article.Id).ToArray();

        var categories = categoryIds.Length == 0
            ? []
            : await dbContext.TicketCategories.AsNoTracking()
                .Where(category => categoryIds.Contains(category.Id))
                .Select(category => new { category.Id, category.Name })
                .ToDictionaryAsync(row => row.Id, row => row.Name, cancellationToken);

        var problems = problemIds.Length == 0
            ? []
            : (await dbContext.Problems.AsNoTracking()
                .Where(problem => problemIds.Contains(problem.Id))
                .Select(problem => new { problem.Id, problem.SequenceNumber })
                .ToListAsync(cancellationToken))
                .ToDictionary(row => row.Id, row => $"PRB-{row.SequenceNumber:000000}");

        var links = await dbContext.TicketKbArticles.AsNoTracking()
            .Where(link => articleIds.Contains(link.ArticleId))
            .GroupBy(link => link.ArticleId)
            .Select(group => new { ArticleId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.ArticleId, row => row.Count, cancellationToken);

        return new KbArticleContext(categories, problems, links);
    }

    private static KbArticleResponse Map(
        KbArticle article,
        KbArticleContext context,
        IReadOnlyList<KbRevisionResponse>? revisions = null) => new(
        article.Id,
        article.Number,
        article.Title,
        article.Summary,
        article.Body,
        article.Keywords,
        article.Status,
        article.CategoryId,
        article.CategoryId is { } categoryId ? context.Categories.GetValueOrDefault(categoryId) : null,
        article.ProblemId,
        article.ProblemId is { } problemId ? context.Problems.GetValueOrDefault(problemId) : null,
        article.Version,
        article.AuthorId,
        article.AuthorName,
        article.PublishedById,
        article.PublishedByName,
        article.PublishedAt,
        article.ArchivedAt,
        article.CreatedAt,
        article.UpdatedAt,
        context.TicketLinks.GetValueOrDefault(article.Id),
        KbWorkflow.NextFrom(article.Status),
        revisions);

    private async Task<IReadOnlyDictionary<string, string[]>?> ReferenceErrorsAsync(
        Guid? categoryId,
        Guid? problemId,
        CancellationToken cancellationToken)
    {
        if (categoryId is { } category
            && !await dbContext.TicketCategories.AnyAsync(item => item.Id == category, cancellationToken))
        {
            return Field(nameof(CreateKbArticleRequest.CategoryId), "That category does not exist.");
        }

        if (problemId is { } problem
            && !await dbContext.Problems.AnyAsync(item => item.Id == problem, cancellationToken))
        {
            return Field(nameof(CreateKbArticleRequest.ProblemId), "That problem does not exist.");
        }

        return null;
    }

    /// <summary>
    /// "KB-000042", "KB 42" and "42" are all the same request, the way WP-1.10 made a ticket number one.
    /// </summary>
    internal static long? ToSequenceNumber(string? search)
    {
        var digits = new string((search ?? string.Empty).Where(char.IsDigit).ToArray());
        return digits.Length is > 0 and <= 18 && long.TryParse(digits, out var value) ? value : null;
    }

    private static IReadOnlyDictionary<string, string[]> Field(string name, string message) =>
        new Dictionary<string, string[]> { [name] = [message] };

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string ActorId(ClaimsPrincipal actor) =>
        ActorRoles.ActorId(actor)
        ?? throw new InvalidOperationException("An authenticated actor identifier is required.");

    private static string ActorDisplayName(ClaimsPrincipal actor) =>
        actor.FindFirst("name")?.Value
        ?? actor.Identity?.Name
        ?? actor.FindFirst("preferred_username")?.Value
        ?? ActorId(actor);

    private sealed record KbArticleContext(
        IReadOnlyDictionary<Guid, string> Categories,
        IReadOnlyDictionary<Guid, string> Problems,
        IReadOnlyDictionary<Guid, int> TicketLinks);
}
