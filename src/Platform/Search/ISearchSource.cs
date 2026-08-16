using System.Security.Claims;

namespace Platform.Search;

/// <summary>
/// One module's contribution to global search, over its own schema and its own full-text index.
/// <para>
/// This is deliberately <b>not</b> a port. A port in <c>Platform/Integration</c> exists for the case where
/// neither of two modules may reference the other, and it is narrow because one module is reaching into
/// another's records. Here nobody reaches into anybody: each module answers about its own tables, and the
/// only thing that sees all five answers is <see cref="ISearchService"/>, which holds no reference to any
/// module and receives them injected. Six ports for a read this wide would also have meant six schemas to
/// migrate in every test host that touches search — the trap eight packages have now met.
/// </para>
/// <para>
/// The consequence worth knowing: adding a source is a registration and nothing else. WP-5.9's knowledge
/// base implements this interface, adds its member to <see cref="SearchResultType"/>, and neither the
/// service nor the endpoint changes.
/// </para>
/// </summary>
public interface ISearchSource
{
    /// <summary>Which group this source fills. Exactly one source per member.</summary>
    SearchResultType Type { get; }

    /// <summary>
    /// Whether this actor may read this kind of record at all, answered without touching the database so
    /// that a forbidden source costs nothing.
    /// <para>
    /// This is the coarse gate and never the whole rule. A source that <em>is</em> visible still narrows
    /// its own query to what this actor may see — an end user can search tickets and must find only their
    /// own, which is a filter in the query rather than a yes or no here (ARCHITECTURE §6).
    /// </para>
    /// </summary>
    bool IsVisibleTo(ClaimsPrincipal actor);

    /// <summary>
    /// Everything this source matches, ranked and capped, with the honest total beside it.
    /// </summary>
    Task<SearchSourceResult> SearchAsync(SearchQuery query, CancellationToken cancellationToken);
}

/// <summary>
/// What a source is asked, after the edge has validated it and turned the typed text into something a
/// query can use.
/// </summary>
/// <param name="Term">Exactly what was typed, trimmed. For error messages and identifier matching.</param>
/// <param name="TsQuery">
/// The AND-ed prefix tsquery from <see cref="SearchTerm.ToPrefixTsQuery"/>. Never null: a term with nothing
/// searchable in it is refused at the edge rather than run as a query that matches everything.
/// </param>
/// <param name="Limit">The per-source cap. Each source applies it to its own results.</param>
public sealed record SearchQuery(string Term, string TsQuery, int Limit, ClaimsPrincipal Actor);
