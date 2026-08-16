using System.Security.Claims;

namespace Platform.Search;

/// <summary>
/// One answer over every module (WP-5.4). Asks each registered <see cref="ISearchSource"/> about its own
/// records and returns the results grouped by kind, each kind ranked, capped and counted on its own.
/// <para>
/// Results are grouped rather than interleaved because ranking across sources is not meaningful:
/// <c>ts_rank</c> is a number about one tsvector against one tsquery, and a ticket's 0.6 and an alert's 0.6
/// are not comparable quantities. Sorting five sources into one list by them would produce an order that
/// looks considered and is arbitrary. Grouping is also what the WP asks the browser to render.
/// </para>
/// </summary>
public sealed class SearchService(IEnumerable<ISearchSource> sources) : ISearchService
{
    /// <summary>
    /// What one group shows when the caller does not say. Small on purpose: the commonest read is a
    /// dropdown under a search box, where five of a kind is already more than a reader scans.
    /// </summary>
    public const int DefaultLimit = 5;

    /// <summary>
    /// The most any one group will return. Each group's own <c>total</c> stays honest past it, so a caller
    /// that hits the cap is told how much it did not get rather than being left to assume it got everything.
    /// </summary>
    public const int MaximumLimit = 50;

    /// <summary>Every kind, in the order groups are rendered. The refusal message names these.</summary>
    public static IReadOnlyList<SearchResultType> AllTypes { get; } =
        [.. Enum.GetValues<SearchResultType>().OrderBy(type => (int)type)];

    public async Task<SearchResponse> SearchAsync(
        SearchRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var limit = Math.Clamp(request.Limit, 1, MaximumLimit);
        var requested = request.Types.Count == 0 ? AllTypes : request.Types;
        var tsQuery = SearchTerm.ToPrefixTsQuery(request.Term)
            ?? throw new ArgumentException("The term holds nothing searchable.", nameof(request));
        var query = new SearchQuery(request.Term.Trim(), tsQuery, limit, actor);

        // Ordered by kind rather than by registration order, so the groups arrive in the same order however
        // the modules happened to be added to the container.
        var byType = sources.ToDictionary(source => source.Type);
        var groups = new List<SearchGroupResponse>(AllTypes.Count);

        // Sequentially, not in parallel. Each source owns its own DbContext and its own connection, so
        // fanning out would work — but these are five small indexed reads against one database, and a
        // search box that opens five connections per keystroke is a worse neighbour than one that opens one.
        foreach (var type in AllTypes)
        {
            if (!byType.TryGetValue(type, out var source))
            {
                // Unreachable while every member has a source, which SearchSourceRegistrationTests pins
                // against the real host. Skipped rather than reported empty, because a group that says
                // "nothing found" for a kind nothing searched is the one lie this response must not tell.
                continue;
            }

            if (!requested.Contains(type))
            {
                groups.Add(Empty(type, SearchSourceStatus.NotRequested));
                continue;
            }

            if (!source.IsVisibleTo(actor))
            {
                groups.Add(Empty(type, SearchSourceStatus.NotPermitted));
                continue;
            }

            var result = await source.SearchAsync(query, cancellationToken);
            groups.Add(new SearchGroupResponse(
                type,
                SearchSourceStatus.Searched,
                result.Hits.Count,
                result.Total,
                result.Total > result.Hits.Count,
                result.Hits));
        }

        var returnedCount = groups.Sum(group => group.Returned);
        var totalCount = groups.Sum(group => group.Total);
        return new SearchResponse(
            query.Term,
            limit,
            requested,
            new SearchSummaryResponse(returnedCount, totalCount, totalCount > returnedCount),
            groups);
    }

    private static SearchGroupResponse Empty(SearchResultType type, SearchSourceStatus status) =>
        new(type, status, 0, 0, false, []);
}
