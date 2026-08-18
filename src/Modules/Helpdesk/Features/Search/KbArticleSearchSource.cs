using System.Security.Claims;

using Microsoft.EntityFrameworkCore;

using Modules.Helpdesk.Data;
using Modules.Helpdesk.Features.Knowledge;

using Platform.Search;

namespace Modules.Helpdesk.Features.Search;

/// <summary>
/// Knowledge articles, from Helpdesk's own <c>helpdesk.kb_articles</c> — the sixth source WP-5.4 named and
/// deliberately left out, because no knowledge base existed to answer with (WP-5.9).
/// <para>
/// Adding it was this class and one registration: neither <see cref="ISearchService"/> nor the endpoint
/// changed, which is what the contribution interface was for.
/// </para>
/// </summary>
public sealed class KbArticleSearchSource(HelpdeskDbContext dbContext) : ISearchSource
{
    public SearchResultType Type => SearchResultType.KbArticle;

    /// <summary>
    /// Everybody, like tickets and unlike the four operator sources. What an end user may <em>find</em> is
    /// narrowed inside the query below to published articles — a yes-or-no gate cannot express "the ones
    /// that are finished", and hiding the group entirely would keep an answer from the person the answer
    /// was written for.
    /// </summary>
    public bool IsVisibleTo(ClaimsPrincipal actor) => true;

    public async Task<SearchSourceResult> SearchAsync(SearchQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var articles = dbContext.KbArticles.AsNoTracking().AsQueryable();

        // Applied before the search predicate and therefore to the count as well: a total that included
        // drafts would leak how many exist while pretending to hide them, which is the leak WP-5.4 closed
        // for an end user's own tickets.
        if (!SearchVisibility.IsAgent(query.Actor))
        {
            articles = articles.Where(article => KbArticleStatuses.Readable.Contains(article.Status));
        }

        var sequenceNumber = KbArticleService.ToSequenceNumber(query.Term);
        articles = articles.Where(article =>
            article.SearchVector.Matches(EF.Functions.ToTsQuery(SearchTerm.Configuration, query.TsQuery))
            || (sequenceNumber != null && article.SequenceNumber == sequenceNumber));

        var total = await articles.CountAsync(cancellationToken);

        var rows = await articles
            .OrderByDescending(article =>
                article.SearchVector.Rank(EF.Functions.ToTsQuery(SearchTerm.Configuration, query.TsQuery)))
            .ThenByDescending(article => article.UpdatedAt)
            .Take(query.Limit)
            .Select(article => new
            {
                article.Id,
                article.SequenceNumber,
                article.Title,
                article.Summary,
                article.Status,
            })
            .ToListAsync(cancellationToken);

        var hits = rows
            .Select(row => new SearchHit(
                SearchResultType.KbArticle,
                row.Id,
                row.Title,
                // Formatted here rather than projected, because KbArticle.Number is `builder.Ignore`d.
                $"KB-{row.SequenceNumber:000000}",
                row.Summary,
                row.Status.ToString()))
            .ToList();

        return new SearchSourceResult(hits, total);
    }
}
