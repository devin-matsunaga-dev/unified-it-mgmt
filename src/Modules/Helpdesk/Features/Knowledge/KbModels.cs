using Modules.Helpdesk.Data;

namespace Modules.Helpdesk.Features.Knowledge;

/// <summary>
/// How a knowledge-base write ended. The same vocabulary <see cref="Problems.ProblemOutcome"/> uses, and
/// for the same reason: a missing body is a 400 about the request, while publishing something already
/// published is a 409 about the state it is in (WP-5.6's "wrong" versus "not now").
/// </summary>
public enum KbOutcome
{
    Success,
    NotFound,
    Invalid,
    InvalidTransition,
    Duplicate,
    Forbidden,
}

// ---- articles ----

/// <param name="ProblemId">
/// Set when the article began as WP-5.7's closing prompt, so the knowledge base records where an answer
/// came from. Optional everywhere else.
/// </param>
public sealed record CreateKbArticleRequest(
    string Title,
    string Summary,
    string Body,
    string? Keywords = null,
    Guid? CategoryId = null,
    Guid? ProblemId = null);

/// <summary>
/// The editable face of an article. Status is absent on purpose — it moves through
/// <c>POST /api/kb-articles/{id}/transitions</c>, following the problem and the change request, because
/// publishing has an entry condition a field assignment would walk straight past.
/// </summary>
public sealed record UpdateKbArticleRequest(
    string Title,
    string Summary,
    string Body,
    string? Keywords = null,
    Guid? CategoryId = null);

public sealed record KbTransitionRequest(KbArticleStatus TargetStatus);

/// <param name="NextStatuses">
/// Where this article can go from here, read off the record rather than duplicated in the browser —
/// WP-5.8's call, taken because WP-5.7's own note records the failure mode of the alternative: a button
/// that is never offered, which nobody reports because nobody knew it should be there.
/// </param>
/// <param name="CategoryName">Resolved at request time from the ticket category tree. Null when uncategorised.</param>
public sealed record KbArticleResponse(
    Guid Id,
    string Number,
    string Title,
    string Summary,
    string Body,
    string? Keywords,
    KbArticleStatus Status,
    Guid? CategoryId,
    string? CategoryName,
    Guid? ProblemId,
    string? ProblemNumber,
    int Version,
    string AuthorId,
    string AuthorName,
    string? PublishedById,
    string? PublishedByName,
    DateTimeOffset? PublishedAt,
    DateTimeOffset? ArchivedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int LinkedTicketCount,
    IReadOnlyList<KbArticleStatus> NextStatuses,
    IReadOnlyList<KbRevisionResponse>? Revisions = null);

/// <summary>
/// One earlier version, as the history panel lists it. The body travels with it because reading an old
/// version is the whole point of keeping one — a list of dates nobody can open is a changelog, not a history.
/// </summary>
public sealed record KbRevisionResponse(
    int Version,
    string Title,
    string Summary,
    string Body,
    string? Keywords,
    string AuthorId,
    string AuthorName,
    DateTimeOffset CreatedAt);

public sealed record KbArticlePageResponse(
    IReadOnlyList<KbArticleResponse> Items,
    int Total,
    int Page,
    int PageSize);

public sealed record KbArticleResult(
    KbOutcome Outcome,
    KbArticleResponse? Article = null,
    string? Error = null,
    IReadOnlyDictionary<string, string[]>? Errors = null);

/// <summary>
/// The browse filter. <c>Statuses</c> is what an agent narrows by; an end user's request is narrowed to
/// published in the query whatever they ask for, because that rule is about disclosure and not about a
/// control (WP-1.8).
/// </summary>
public sealed record KbArticleListFilter(
    string? Search = null,
    IReadOnlyList<KbArticleStatus>? Statuses = null,
    Guid? CategoryId = null,
    int Page = 1,
    int PageSize = 25);

// ---- suggestions ----

/// <summary>
/// What somebody has typed so far, asked of the knowledge base. Both halves optional because the question
/// is asked while a form is being filled in — a subject with an empty body is the commonest moment for it.
/// </summary>
public sealed record KbSuggestionRequest(
    string? Subject = null,
    string? Body = null,
    Guid? CategoryId = null,
    int Limit = 5);

/// <param name="Rank">
/// The <c>ts_rank</c> of this article against the text. Carried for ordering and for tests, never rendered
/// as a percentage: WP-5.4 established that a rank is a number about one document and one query, and
/// dressing it up as a confidence score invites somebody to compare two of them.
/// </param>
public sealed record KbSuggestionResponse(
    Guid Id,
    string Number,
    string Title,
    string Summary,
    string? CategoryName,
    DateTimeOffset? PublishedAt,
    double Rank);

// ---- ticket links ----

public sealed record LinkKbArticleRequest(Guid ArticleId);

/// <summary>An article attached to a ticket, as the ticket screen renders it.</summary>
public sealed record TicketKbArticleResponse(
    Guid ArticleId,
    string Number,
    string Title,
    string Summary,
    KbArticleStatus Status,
    string LinkedById,
    string LinkedByName,
    DateTimeOffset LinkedAt);

public sealed record TicketKbArticleResult(
    KbOutcome Outcome,
    TicketKbArticleResponse? Link = null,
    string? Error = null,
    IReadOnlyDictionary<string, string[]>? Errors = null);
