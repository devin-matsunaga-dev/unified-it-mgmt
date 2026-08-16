using System.Security.Claims;

using Microsoft.EntityFrameworkCore;

using Modules.Assets.Data;

using Platform.Search;

namespace Modules.Assets.Features.Search;

/// <summary>
/// CMDB records, from Assets' own <c>assets.cis</c> (WP-5.4). Matched on the weighted tsvector the table
/// generates — name and identifiers first, hostnames and addresses next, description last.
/// </summary>
public sealed class CiSearchSource(AssetsDbContext dbContext) : ISearchSource
{
    public SearchResultType Type => SearchResultType.Ci;

    /// <summary>
    /// Agents only, matching every other CMDB read since WP-2.1. An end user owns assets and sees their own
    /// on the portal, but the estate is not theirs to search: a serial number would otherwise be enough to
    /// find out what somebody else was issued and where they sit.
    /// </summary>
    public bool IsVisibleTo(ClaimsPrincipal actor) => SearchVisibility.IsAgent(actor);

    public async Task<SearchSourceResult> SearchAsync(
        SearchQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        // The WP's own verification step is "serial number finds the CI", and full-text search alone is not
        // a safe way to keep that promise: how the parser splits a punctuated serial depends on the
        // punctuation, and a term the search box split differently matches nothing at all with nothing to
        // say anything went wrong. Both identifier columns are therefore compared directly as well, which
        // is what WP-1.10 already does for a ticket number.
        var identifier = SearchTerm.ToIdentifier(query.Term);
        var exact = identifier is null ? null : SearchTerm.EscapeLike(identifier);

        var cis = dbContext.Cis.Where(ci =>
            ci.SearchVector.Matches(EF.Functions.ToTsQuery(SearchTerm.Configuration, query.TsQuery))
            || (exact != null && ci.AssetTag != null && EF.Functions.ILike(ci.AssetTag, exact))
            || (exact != null && ci.SerialNumber != null && EF.Functions.ILike(ci.SerialNumber, exact)));

        var total = await cis.CountAsync(cancellationToken);

        // Projected to a row and turned into hits afterwards: the lifecycle state is an enum behind a string
        // conversion, and calling ToString() on it inside the projection is the kind of expression that
        // compiles and then fails as a 500 at run time (the shape WP-4.3 met with the TPH discriminator).
        var rows = await cis
            .OrderByDescending(ci =>
                ci.SearchVector.Rank(EF.Functions.ToTsQuery(SearchTerm.Configuration, query.TsQuery)))
            .ThenBy(ci => ci.Name)
            .Take(query.Limit)
            .Select(ci => new
            {
                ci.Id,
                ci.Name,
                // The tag is what is on the sticker; the serial is what is on the chassis when there is no
                // sticker. One of the two, because the row has room for one identifier.
                Reference = ci.AssetTag ?? ci.SerialNumber,
                ci.SiteName,
                ci.LifecycleState,
            })
            .ToListAsync(cancellationToken);

        var hits = rows
            .Select(row => new SearchHit(
                SearchResultType.Ci,
                row.Id,
                row.Name,
                row.Reference,
                row.SiteName,
                // The raw enum name, not a sentence. The browser already labels and colours a lifecycle
                // state everywhere else it prints one, and a spelling composed here would be a third copy of
                // a vocabulary this module already mirrors by hand once.
                row.LifecycleState.ToString()))
            .ToList();

        return new SearchSourceResult(hits, total);
    }
}
