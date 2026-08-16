using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;

using Microsoft.Extensions.DependencyInjection;

using Platform.Search;

namespace Infrastructure.Tests;

/// <summary>
/// Global search end to end (WP-5.4): one call to <c>GET /api/search</c> reaching five modules' schemas and
/// coming back as one grouped, ranked, capped answer.
/// <para>
/// <b>The shared-table trap in its newest shape, and this is the worst case of it yet.</b> Every read before
/// this one was rooted at something — a CI, an alert, a site — so it could not collide with what other
/// classes wrote. A search is rooted at a <em>word</em>, over a database the whole suite shares. Every
/// record here therefore carries one nonsense token that appears nowhere else in the solution
/// (<see cref="Marker"/>), and every assertion searches for it. Searching for "switch" here would assert
/// against whatever the other forty classes happened to have created.
/// </para>
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class SearchApiIntegrationTests(InfrastructureFixture infrastructure, SearchEstateFixture estate)
    : IClassFixture<SearchEstateFixture>, IAsyncLifetime
{
    /// <summary>
    /// The token that makes an estate-wide read testable against a shared database. Nonsense on purpose: a
    /// real word would match another class's fixtures, and — as WP-4.1 learned writing a "no secret in the
    /// output" assertion — a term that appears anywhere else cannot fail for the reason you think it can.
    /// </summary>
    internal const string Marker = "zqrtylph";

    private HttpClient _client = null!;
    private Estate _estate = null!;

    /// <summary>
    /// The host and the records belong to the class fixture, built on the first test through and reused by
    /// the rest. See <see cref="SearchEstateFixture"/> for why that is not done here.
    /// </summary>
    public async Task InitializeAsync()
    {
        ArgumentNullException.ThrowIfNull(estate);
        await estate.EnsureInitialisedAsync(infrastructure);
        _client = estate.Client;
        _estate = estate.Records;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// The whole point of the feature: one word, five modules, one answer. Every source contributes and
    /// every group comes back searched.
    /// </summary>
    [Fact]
    public async Task Search_ForAWordEverySourceHolds_ReturnsAGroupFromEachOfThem()
    {
        var response = await SearchAsync($"?q={Marker}&limit=50");

        Assert.All(response.Groups, group => Assert.Equal("Searched", group.Status));
        Assert.Equal(
            ["Ticket", "Ci", "Device", "Alert", "User"],
            response.Groups.Select(group => group.Type));
        Assert.All(response.Groups, group => Assert.NotEmpty(group.Hits));
        Assert.Contains(Hits(response, "Ticket"), hit => hit.Id == _estate.TicketId);
        Assert.Contains(Hits(response, "Ci"), hit => hit.Id == _estate.CiId);
        Assert.Contains(Hits(response, "Device"), hit => hit.Id == _estate.DeviceId);
        Assert.Contains(Hits(response, "Alert"), hit => hit.Id == _estate.AlertId);
        Assert.Contains(Hits(response, "User"), hit => hit.Id == _estate.UserId);
    }

    /// <summary>
    /// WP-5.4's first verification step. Asserted through the API rather than against the tsvector, because
    /// what it really checks is that a serial survives a route no full-text parser is involved in.
    /// </summary>
    [Fact]
    public async Task Search_ForASerialNumber_FindsTheCi()
    {
        var response = await SearchAsync($"?q={_estate.SerialNumber}&types=ci");

        var hit = Assert.Single(Hits(response, "Ci"));
        Assert.Equal(_estate.CiId, hit.Id);
        Assert.Equal(_estate.AssetTag, hit.Reference);
    }

    /// <summary>
    /// The same guarantee for the punctuated shape full-text search cannot be trusted with. A dotted or
    /// hyphenated identifier tokenises differently from the way the search box splits it, so the direct
    /// comparison is what stands between "found it" and an empty answer nothing explains.
    /// </summary>
    [Fact]
    public async Task Search_ForAPunctuatedAssetTag_StillFindsTheCi()
    {
        var response = await SearchAsync($"?q={_estate.AssetTag}&types=ci");

        Assert.Contains(Hits(response, "Ci"), hit => hit.Id == _estate.CiId);
    }

    /// <summary>
    /// The address case, which is the one an operator hits hourly and the one the tsvector gets wrong on its
    /// own: Postgres keeps <c>10.x.y.z</c> as a single lexeme and the search box asks for four prefixes,
    /// none of which can match it.
    /// </summary>
    [Fact]
    public async Task Search_ForADevicesIpAddress_FindsTheDevice()
    {
        var response = await SearchAsync($"?q={_estate.Address}&types=device");

        var hit = Assert.Single(Hits(response, "Device"));
        Assert.Equal(_estate.DeviceId, hit.Id);
        Assert.Equal(_estate.Address, hit.Title);
    }

    /// <summary>
    /// WP-5.4's second verification step: a requester's name finds their tickets <em>and</em> the person.
    /// Two groups from one word, which is the case the grouped shape exists for.
    /// </summary>
    [Fact]
    public async Task Search_ForARequesterName_FindsBothTheirTicketsAndThePerson()
    {
        var response = await SearchAsync($"?q={_estate.RequesterName}&limit=50");

        Assert.Contains(Hits(response, "Ticket"), hit => hit.Id == _estate.TicketId);
        var person = Assert.Single(Hits(response, "User"), hit => hit.Id == _estate.UserId);
        Assert.Equal(_estate.RequesterName, person.Title);
    }

    /// <summary>A ticket is found by the number somebody pasted.</summary>
    [Fact]
    public async Task Search_ForATicketNumber_FindsThatTicket()
    {
        var response = await SearchAsync($"?q={_estate.TicketNumber}&types=ticket");

        var hit = Assert.Single(Hits(response, "Ticket"));
        Assert.Equal(_estate.TicketId, hit.Id);
        Assert.Equal(_estate.TicketNumber, hit.Reference);
    }

    /// <summary>
    /// WP-5.4's third verification step. A term nothing matches is a clean, complete, empty answer — every
    /// group searched and every group honestly zero — rather than an error or a missing group.
    /// </summary>
    [Fact]
    public async Task Search_ForGibberish_IsAnEmptyAnswerRatherThanAFailure()
    {
        var response = await SearchAsync("?q=qwlkjhasdfmnbvzxc");

        Assert.Equal(5, response.Groups.Count);
        Assert.All(response.Groups, group =>
        {
            Assert.Equal("Searched", group.Status);
            Assert.Empty(group.Hits);
            Assert.Equal(0, group.Total);
        });
        Assert.Equal(0, response.Summary.TotalCount);
        Assert.False(response.Summary.Truncated);
    }

    /// <summary>
    /// WP-5.4's fourth verification step, and the one this package had no precedent for: an end user finds
    /// their own ticket and not the identically-worded one beside it.
    /// </summary>
    [Fact]
    public async Task Search_AsAnEndUser_FindsTheirOwnTicketAndNotSomebodyElses()
    {
        var response = await SearchAsync(
            $"?q={Marker}&limit=50", role: "EndUser", subject: _estate.RequesterId);

        var tickets = Assert.Single(response.Groups, group => group.Type == "Ticket");
        Assert.Equal("Searched", tickets.Status);
        Assert.Contains(tickets.Hits, hit => hit.Id == _estate.TicketId);
        Assert.DoesNotContain(tickets.Hits, hit => hit.Id == _estate.OtherRequestersTicketId);

        // And the count is narrowed too. A total that included the tickets it refused to list would leak how
        // many of them there are while pretending to hide them.
        Assert.Equal(tickets.Hits.Count, tickets.Total);
    }

    /// <summary>
    /// The other half of the same rule: the four agent sources are refused outright, and refused in a way
    /// that cannot be read as "the estate holds nothing like that". <c>NotPermitted</c> rather than an empty
    /// <c>Searched</c> is the whole distinction.
    /// </summary>
    [Fact]
    public async Task Search_AsAnEndUser_ReportsTheAgentSourcesAsForbiddenRatherThanEmpty()
    {
        var response = await SearchAsync($"?q={Marker}", role: "EndUser", subject: _estate.RequesterId);

        foreach (var type in new[] { "Ci", "Device", "Alert", "User" })
        {
            var group = Assert.Single(response.Groups, item => item.Type == type);
            Assert.Equal("NotPermitted", group.Status);
            Assert.Empty(group.Hits);
            Assert.Equal(0, group.Total);
        }
    }

    /// <summary>
    /// The filter pushes into the sources rather than trimming the merge, following WP-5.3: the excluded
    /// kinds are never queried and say so. Filtering afterwards would make "assets only" the newest five of
    /// everything, filtered — which on a busy estate comes back empty and looks like a broken filter.
    /// </summary>
    [Fact]
    public async Task Search_FilteredToOneKind_SaysTheOtherSourcesWereNotAsked()
    {
        var response = await SearchAsync($"?q={Marker}&types=ci");

        Assert.Equal(["Ci"], response.Types);
        var cis = Assert.Single(response.Groups, group => group.Type == "Ci");
        Assert.Equal("Searched", cis.Status);
        Assert.NotEmpty(cis.Hits);

        foreach (var type in new[] { "Ticket", "Device", "Alert", "User" })
        {
            Assert.Equal("NotRequested", Assert.Single(response.Groups, g => g.Type == type).Status);
        }
    }

    /// <summary>
    /// The cap is per source and the total past it is honest, so a list of one says two. One cap over the
    /// merged answer is what this guards against.
    /// </summary>
    [Fact]
    public async Task Search_WithASmallLimit_CapsEachSourceSeparatelyAndStatesTheRealTotals()
    {
        var response = await SearchAsync($"?q={Marker}&limit=1");

        Assert.Equal(1, response.Limit);
        var cis = Assert.Single(response.Groups, group => group.Type == "Ci");
        Assert.Single(cis.Hits);
        Assert.Equal(_estate.CiCount, cis.Total);
        Assert.True(cis.Truncated);

        // The other sources are capped on their own account rather than sharing one budget: a ticket is
        // still there beside the single CI, which is the property one cap over the merge would destroy.
        var tickets = Assert.Single(response.Groups, group => group.Type == "Ticket");
        Assert.Single(tickets.Hits);
        Assert.Equal(_estate.TicketCount, tickets.Total);
        Assert.True(response.Summary.Truncated);
    }

    /// <summary>
    /// Ranking within a group, which is what setweight was added for: the CI whose <em>name</em> carries the
    /// word beats the CI that merely mentions it in its description.
    /// </summary>
    [Fact]
    public async Task Search_WhereOneCiIsNamedForTheTermAndAnotherOnlyMentionsIt_RanksTheNameFirst()
    {
        var response = await SearchAsync($"?q={Marker}&types=ci&limit=50");

        var hits = Hits(response, "Ci");
        var named = hits.FindIndex(hit => hit.Id == _estate.CiId);
        var mentioned = hits.FindIndex(hit => hit.Id == _estate.CiMentionedInDescriptionId);
        Assert.True(named >= 0 && mentioned >= 0, "Both CIs should match the marker.");
        Assert.True(
            named < mentioned,
            $"Expected the named CI above the one that only mentions it, got {named} and {mentioned}.");
    }

    /// <summary>
    /// A cleared alert is still findable — the board answers what is broken now, and a search box is where
    /// somebody looks for the alert they are being asked about — but the open one sorts above it.
    /// </summary>
    [Fact]
    public async Task Search_OverAlerts_FindsClearedOnesTooAndPutsTheOpenOneFirst()
    {
        var response = await SearchAsync($"?q={Marker}&types=alert&limit=50");

        var hits = Hits(response, "Alert");
        Assert.Contains(hits, hit => hit.Id == _estate.AlertId);
        var cleared = Assert.Single(hits, hit => hit.Id == _estate.ClearedAlertId);
        Assert.Contains("cleared", cleared.Subtitle!, StringComparison.Ordinal);
        Assert.True(
            hits.FindIndex(hit => hit.Id == _estate.AlertId) < hits.IndexOf(cleared),
            "The open alert should sort above the cleared one.");
    }

    /// <summary>
    /// Every kind in the vocabulary has a source behind it, asserted against the host that really runs. A
    /// member without one would answer a whole class of question with a confident "nothing found", and
    /// nothing else in the suite would notice.
    /// </summary>
    [Fact]
    public void EverySearchResultType_HasExactlyOneRegisteredSource()
    {
        using var scope = estate.Services.CreateScope();
        var registered = scope.ServiceProvider.GetServices<ISearchSource>().Select(source => source.Type);

        Assert.Equal(Enum.GetValues<SearchResultType>().Order(), registered.Order());
    }

    /// <summary>
    /// The failure path for the term: nothing searchable is a refusal, not five empty groups. A 200 there
    /// would say "the estate holds nothing like that" about a question nobody asked.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("?q=")]
    [InlineData("?q=%20%20")]
    [InlineData("?q=%26%7C()")]
    public async Task Search_WithNothingSearchableInTheTerm_Is400(string queryString)
    {
        using var request = Authenticated($"/api/search{queryString}");
        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("q", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The failure path for the filter, taken from WP-5.3 because the reasoning is identical: a filter the
    /// server silently dropped answers a different question and looks exactly like a filter that is broken.
    /// </summary>
    [Fact]
    public async Task Search_WithAnUnrecognisedTypeFilter_Is400NamingTheKindsItAccepts()
    {
        using var request = Authenticated($"/api/search?q={Marker}&types=assets");
        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadAsStringAsync();
        Assert.Contains("types", problem, StringComparison.Ordinal);
        Assert.Contains("Ticket", problem, StringComparison.Ordinal);
        Assert.Contains("User", problem, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same refusal for a number, which is the shape that gets through by accident: <c>Enum.TryParse</c>
    /// accepts any integer. Without the definedness check <c>?types=99</c> parses to a kind that does not
    /// exist, matches no source, and answers with nothing found and no complaint — the trap WP-5.3 hit and
    /// left a standing warning about.
    /// </summary>
    [Fact]
    public async Task Search_WithATypeFilterThatIsAnUndefinedNumber_Is400RatherThanAnEmptyAnswer()
    {
        using var request = Authenticated($"/api/search?q={Marker}&types=99");
        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>An unauthenticated caller gets nothing, which is the only gate this endpoint has of its own.</summary>
    [Fact]
    public async Task Search_WithoutAToken_Is401()
    {
        using var response = await _client.GetAsync(new Uri($"/api/search?q={Marker}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static List<SearchHitDto> Hits(SearchDto response, string type) =>
        response.Groups.Single(group => group.Type == type).Hits;

    private async Task<SearchDto> SearchAsync(
        string queryString,
        string role = "Technician",
        string? subject = null)
    {
        using var request = Authenticated($"/api/search{Escape(queryString)}", role, subject);
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Assert.IsType<SearchDto>(await response.Content.ReadFromJsonAsync<SearchDto>());
    }

    /// <summary>
    /// Percent-encodes the term, because a term is arbitrary text. A requester's name has a space in it, and
    /// an unencoded space truncates the query string without failing — which would have made the requester
    /// test pass on the first word and prove nothing.
    /// </summary>
    private static string Escape(string queryString) => Regex.Replace(
        queryString,
        "q=([^&]*)",
        match => "q=" + Uri.EscapeDataString(Uri.UnescapeDataString(match.Groups[1].Value)));

    private static HttpRequestMessage Authenticated(
        string uri,
        string role = "Technician",
        string? subject = null) =>
        SearchEstateFixture.Authenticate(new HttpRequestMessage(HttpMethod.Get, uri), role, subject);

    private sealed record SearchHitDto(
        string Type, Guid Id, string Title, string? Reference, string? Subtitle, string? Badge);

    private sealed record SearchGroupDto(
        string Type, string Status, int Returned, int Total, bool Truncated, List<SearchHitDto> Hits);

    private sealed record SearchSummaryDto(int ReturnedCount, int TotalCount, bool Truncated);

    private sealed record SearchDto(
        string Term, int Limit, List<string> Types, SearchSummaryDto Summary, List<SearchGroupDto> Groups);
}
