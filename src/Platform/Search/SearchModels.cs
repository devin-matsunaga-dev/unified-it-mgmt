namespace Platform.Search;

/// <summary>
/// The kinds of record global search can reach, and the filter's whole vocabulary — <c>?types=ticket,ci</c>
/// is this enum and nothing else.
/// <para>
/// The declaration order is the order the groups are rendered in: the two things an operator is most often
/// hunting for by name, then the two monitoring surfaces, then people. Every member has exactly one
/// registered <see cref="ISearchSource"/> behind it, which
/// <c>SearchSourceRegistrationTests</c> asserts against the real host — a member with no source would
/// answer "nothing found" for a whole class of record, which is the one wrong answer a search box can give.
/// </para>
/// <para>
/// WP-5.4's own text names a sixth source, the knowledge base. There is no KB entity anywhere in this
/// solution — WP-5.9 builds it — so there is deliberately no member for it here: an unimplemented member
/// would report a real question ("is there an article about this?") as a confident no.
/// </para>
/// </summary>
public enum SearchResultType
{
    /// <summary>Work somebody raised. <c>helpdesk.tickets</c>.</summary>
    Ticket = 1,

    /// <summary>A CMDB record. <c>assets.cis</c>.</summary>
    Ci = 2,

    /// <summary>Something the platform polls. <c>monitoring.monitored_devices</c>.</summary>
    Device = 3,

    /// <summary>Something monitoring found wrong. <c>monitoring.alerts</c>.</summary>
    Alert = 4,

    /// <summary>A person. <c>platform.user_profiles</c>.</summary>
    User = 5,
}

/// <summary>Why a group is empty, which is three different statements and never one.</summary>
public enum SearchSourceStatus
{
    /// <summary>The source ran. A zero here is a fact about the estate.</summary>
    Searched = 1,

    /// <summary>
    /// The caller's <c>types</c> filter excluded it, so it was never queried. Its counts are zero because
    /// nothing asked rather than because nothing matched — WP-5.3's rule, and the reason a filtered search
    /// can say "not shown" instead of leaving somebody to read it as an empty estate.
    /// </summary>
    NotRequested = 2,

    /// <summary>
    /// The caller may not read this kind of record at all. Distinct from <see cref="Searched"/> with no
    /// hits for the same reason: an end user searching an asset tag must not be told the CMDB is empty.
    /// </summary>
    NotPermitted = 3,
}

/// <summary>
/// One thing that matched, in the shape the results list renders it.
/// <para>
/// Deliberately one shape for five sources rather than five payloads: the dropdown shows every kind in one
/// list and a reader moves through them with one pair of arrow keys, so a row that renders differently per
/// kind would be five components and five keyboard behaviours. What varies between kinds is which of the
/// optional fields is set.
/// </para>
/// </summary>
/// <param name="Id">
/// The source row's own id, which is also what the browser routes on. No URL travels: routes are the SPA's
/// own business and it already owns the map (see <c>searchUi.ts</c>), the same split WP-5.3 made when the
/// timeline sent a <c>ticketId</c> rather than a link.
/// </param>
/// <param name="Reference">
/// The identifier a person would quote out loud — <c>INC-000042</c>, an asset tag, the address a device is
/// polled at. Null where the kind has none.
/// </param>
/// <param name="Subtitle">One line of context: who raised it, where it lives, what it is attached to.</param>
/// <param name="Badge">
/// The single status word this kind is triaged by — a ticket's status, a CI's lifecycle state, an alert's
/// severity, a person's role. Rendered as a pill by the browser, which owns the colour (DESIGN §3).
/// </param>
public sealed record SearchHit(
    SearchResultType Type,
    Guid Id,
    string Title,
    string? Reference,
    string? Subtitle,
    string? Badge);

/// <summary>
/// What one source found.
/// </summary>
/// <param name="Total">
/// Everything it matched, cap or no cap. The honest number, so a dropdown showing five of ninety says
/// ninety — WP-2.4's rule that a truncated answer must never look like a complete one.
/// </param>
public sealed record SearchSourceResult(IReadOnlyList<SearchHit> Hits, int Total);

/// <summary>One kind's results, and — when there are none — which of the three reasons applies.</summary>
public sealed record SearchGroupResponse(
    SearchResultType Type,
    SearchSourceStatus Status,
    int Returned,
    int Total,
    bool Truncated,
    IReadOnlyList<SearchHit> Hits);

/// <param name="TotalCount">Across every searched source, including what the caps left out.</param>
public sealed record SearchSummaryResponse(int ReturnedCount, int TotalCount, bool Truncated);

/// <summary>
/// One answer over every module: what matched, grouped by kind, each kind ranked and counted on its own.
/// </summary>
/// <param name="Limit">
/// The per-source cap that was applied, echoed so a truncated answer explains itself. Per source and never
/// across the merge, following WP-5.3: one cap over everything would let the noisiest kind push every other
/// kind out of its own results, and the estate with the most alerts is exactly the one whose tickets you
/// then could not find.
/// </param>
public sealed record SearchResponse(
    string Term,
    int Limit,
    IReadOnlyList<SearchResultType> Types,
    SearchSummaryResponse Summary,
    IReadOnlyList<SearchGroupResponse> Groups);

/// <summary>What the endpoint asks the service for, once the edge has validated it.</summary>
public sealed record SearchRequest(string Term, IReadOnlyList<SearchResultType> Types, int Limit);
