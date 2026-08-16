using System.Security.Claims;

using Platform.Search;

namespace Infrastructure.Tests;

/// <summary>
/// The merge on its own, over hand-written sources and no database (WP-5.4). What the real sources return
/// is <see cref="SearchApiIntegrationTests"/>'s job; what is here is the arithmetic that decides how five
/// answers become one — the three reasons a group can be empty, the caps, and the honest totals.
/// </summary>
public sealed class SearchServiceTests
{
    private static readonly ClaimsPrincipal Technician = Principal("Technician");
    private static readonly ClaimsPrincipal EndUser = Principal("EndUser");

    [Fact]
    public async Task Search_WithNoTypeFilter_AsksEveryVisibleSourceAndGroupsTheAnswers()
    {
        var service = Build(
            new FakeSource(SearchResultType.Ticket, Hits(SearchResultType.Ticket, 2), total: 2),
            new FakeSource(SearchResultType.Ci, Hits(SearchResultType.Ci, 1), total: 1),
            new FakeSource(SearchResultType.Device, [], total: 0),
            new FakeSource(SearchResultType.Alert, [], total: 0),
            new FakeSource(SearchResultType.User, Hits(SearchResultType.User, 1), total: 1));

        var response = await service.SearchAsync(Request("core"), Technician, default);

        Assert.All(response.Groups, group => Assert.Equal(SearchSourceStatus.Searched, group.Status));
        Assert.Equal(4, response.Summary.ReturnedCount);
        Assert.Equal(4, response.Summary.TotalCount);
        Assert.False(response.Summary.Truncated);
    }

    /// <summary>
    /// Groups arrive in the enum's order however the container happened to hand the sources over, so the
    /// results list does not reshuffle itself between deployments.
    /// </summary>
    [Fact]
    public async Task Search_WhateverOrderTheSourcesAreRegisteredIn_ReturnsTheGroupsInTypeOrder()
    {
        var service = Build(
            new FakeSource(SearchResultType.User, [], total: 0),
            new FakeSource(SearchResultType.Alert, [], total: 0),
            new FakeSource(SearchResultType.Ticket, [], total: 0),
            new FakeSource(SearchResultType.Device, [], total: 0),
            new FakeSource(SearchResultType.Ci, [], total: 0));

        var response = await service.SearchAsync(Request("core"), Technician, default);

        Assert.Equal(
            [
                SearchResultType.Ticket, SearchResultType.Ci, SearchResultType.Device,
                SearchResultType.Alert, SearchResultType.User,
            ],
            response.Groups.Select(group => group.Type));
    }

    /// <summary>
    /// WP-5.3's rule, restated for search: a source the filter excluded is never queried, and it says so
    /// rather than reporting zero. Zero is a claim about the estate; this is a fact about the request.
    /// </summary>
    [Fact]
    public async Task Search_FilteredToOneType_QueriesOnlyThatSourceAndSaysTheOthersWereNotAsked()
    {
        var tickets = new FakeSource(SearchResultType.Ticket, Hits(SearchResultType.Ticket, 1), total: 1);
        var cis = new FakeSource(SearchResultType.Ci, Hits(SearchResultType.Ci, 9), total: 9);
        var service = Build(tickets, cis);

        var response = await service.SearchAsync(
            Request("core", SearchResultType.Ticket),
            Technician,
            default);

        Assert.Equal(1, tickets.Calls);
        Assert.Equal(0, cis.Calls);

        var group = Assert.Single(response.Groups, item => item.Type == SearchResultType.Ci);
        Assert.Equal(SearchSourceStatus.NotRequested, group.Status);
        Assert.Equal(0, group.Total);
        Assert.Empty(group.Hits);
    }

    /// <summary>
    /// The distinction WP-5.4 adds, and the one an end user's search depends on: a source they may not read
    /// is not a source that found nothing. Reporting it as searched-and-empty would tell an end user the
    /// CMDB holds no such asset, which is both a lie and a hint about what is in it.
    /// </summary>
    [Fact]
    public async Task Search_AsAnActorWithoutTheRole_ReportsTheSourceAsForbiddenRatherThanEmpty()
    {
        var cis = new FakeSource(SearchResultType.Ci, Hits(SearchResultType.Ci, 3), total: 3, agentsOnly: true);
        var service = Build(new FakeSource(SearchResultType.Ticket, [], total: 0), cis);

        var response = await service.SearchAsync(Request("core"), EndUser, default);

        Assert.Equal(0, cis.Calls);
        var group = Assert.Single(response.Groups, item => item.Type == SearchResultType.Ci);
        Assert.Equal(SearchSourceStatus.NotPermitted, group.Status);
        Assert.Equal(0, group.Total);
    }

    /// <summary>
    /// The cap is per source, following WP-5.3. One cap across the merge would let the source with the most
    /// matches push every other kind out of the results — and on an estate mid-outage the noisiest source
    /// is the alerts, so the ticket somebody was hunting for is exactly what would disappear.
    /// </summary>
    [Fact]
    public async Task Search_WhenOneSourceHasFarMoreMatches_DoesNotLetItCrowdOutTheOthers()
    {
        var service = Build(
            new FakeSource(SearchResultType.Ticket, Hits(SearchResultType.Ticket, 2), total: 2),
            new FakeSource(SearchResultType.Alert, Hits(SearchResultType.Alert, 5), total: 400));

        var response = await service.SearchAsync(Request("core"), Technician, default);

        Assert.Equal(2, Assert.Single(response.Groups, g => g.Type == SearchResultType.Ticket).Returned);
        var alerts = Assert.Single(response.Groups, g => g.Type == SearchResultType.Alert);
        Assert.Equal(5, alerts.Returned);
        // The honest number survives the cap — WP-2.4's rule — so a list of five says four hundred.
        Assert.Equal(400, alerts.Total);
        Assert.True(alerts.Truncated);
        Assert.True(response.Summary.Truncated);
        Assert.Equal(402, response.Summary.TotalCount);
    }

    /// <summary>An out-of-range limit is clamped rather than refused, and the response echoes what it used.</summary>
    [Theory]
    [InlineData(0, 1)]
    [InlineData(-4, 1)]
    [InlineData(5_000, SearchService.MaximumLimit)]
    public async Task Search_WithALimitOutsideTheRange_ClampsItAndSaysWhichOneItApplied(int asked, int expected)
    {
        var source = new FakeSource(SearchResultType.Ticket, [], total: 0);
        var service = Build(source);

        var response = await service.SearchAsync(
            new SearchRequest("core", [], asked),
            Technician,
            default);

        Assert.Equal(expected, response.Limit);
        Assert.Equal(expected, source.LastLimit);
    }

    /// <summary>
    /// A kind with no registered source is left out of the response entirely rather than reported empty.
    /// Unreachable while every member has one — <see cref="SearchApiIntegrationTests"/> pins that against
    /// the real host — but the behaviour is what makes adding WP-5.9's knowledge base a registration.
    /// </summary>
    [Fact]
    public async Task Search_WithAKindThatHasNoSource_LeavesItOutRatherThanReportingItEmpty()
    {
        var service = Build(new FakeSource(SearchResultType.Ticket, [], total: 0));

        var response = await service.SearchAsync(Request("core"), Technician, default);

        Assert.Equal([SearchResultType.Ticket], response.Groups.Select(group => group.Type));
    }

    /// <summary>
    /// The failure path: a term the edge should have refused must not reach a source as an empty tsquery,
    /// which Postgres would treat as matching nothing while looking like a search that ran.
    /// </summary>
    [Fact]
    public async Task Search_WithATermHoldingNothingSearchable_Throws()
    {
        var service = Build(new FakeSource(SearchResultType.Ticket, [], total: 0));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.SearchAsync(Request(" &|() "), Technician, default));
    }

    private static SearchService Build(params ISearchSource[] sources) => new(sources);

    private static SearchRequest Request(string term, params SearchResultType[] types) =>
        new(term, types, SearchService.DefaultLimit);

    private static IReadOnlyList<SearchHit> Hits(SearchResultType type, int count) =>
        [.. Enumerable.Range(0, count).Select(index => new SearchHit(
            type, Guid.CreateVersion7(), $"{type} {index}", null, null, null))];

    private static ClaimsPrincipal Principal(string role) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.Role, role), new Claim("sub", "search-tests")], "Test"));

    private sealed class FakeSource(
        SearchResultType type,
        IReadOnlyList<SearchHit> hits,
        int total,
        bool agentsOnly = false) : ISearchSource
    {
        public SearchResultType Type => type;

        public int Calls { get; private set; }

        public int LastLimit { get; private set; }

        public bool IsVisibleTo(ClaimsPrincipal actor) => !agentsOnly || SearchVisibility.IsAgent(actor);

        public Task<SearchSourceResult> SearchAsync(SearchQuery query, CancellationToken cancellationToken)
        {
            Calls++;
            LastLimit = query.Limit;
            return Task.FromResult(new SearchSourceResult([.. hits.Take(query.Limit)], total));
        }
    }
}
