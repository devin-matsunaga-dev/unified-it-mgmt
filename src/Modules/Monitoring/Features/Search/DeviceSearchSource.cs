using System.Security.Claims;

using Microsoft.EntityFrameworkCore;

using Modules.Monitoring.Data;

using Platform.Search;

namespace Modules.Monitoring.Features.Search;

/// <summary>
/// Monitored devices, from Monitoring's own <c>monitoring.monitored_devices</c> (WP-5.4).
/// <para>
/// A device is named here by the address it is polled at, which is WP-5.3's call restated: a device has no
/// name of its own, it borrows the CI's, and the CI is a record in another module's schema that this source
/// may not join to. Somebody hunting for a device by its CI's name finds the CI, whose page links to it.
/// </para>
/// </summary>
public sealed class DeviceSearchSource(MonitoringDbContext dbContext) : ISearchSource
{
    public SearchResultType Type => SearchResultType.Device;

    /// <summary>Agents only, matching every monitoring surface since WP-3.1.</summary>
    public bool IsVisibleTo(ClaimsPrincipal actor) => SearchVisibility.IsAgent(actor);

    public async Task<SearchSourceResult> SearchAsync(
        SearchQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        // An address is the one thing this source is most often searched by and the one thing full-text
        // search is worst at. "10.10.0.5" is a single lexeme to the parser and four prefix terms to the
        // search box, and no lexeme begins "0" — so without this, typing a device's own IP address finds
        // nothing. Matched as a prefix rather than exactly, because half an address is how somebody looks
        // for a subnet.
        var identifier = SearchTerm.ToIdentifier(query.Term);
        var pattern = identifier is null ? null : SearchTerm.EscapeLike(identifier) + "%";

        var devices = dbContext.MonitoredDevices.Where(device =>
            device.SearchVector.Matches(EF.Functions.ToTsQuery(SearchTerm.Configuration, query.TsQuery))
            || (pattern != null && EF.Functions.ILike(device.Address, pattern)));

        var total = await devices.CountAsync(cancellationToken);
        var hits = await devices
            .OrderByDescending(device =>
                device.SearchVector.Rank(EF.Functions.ToTsQuery(SearchTerm.Configuration, query.TsQuery)))
            .ThenBy(device => device.Address)
            .Take(query.Limit)
            .Select(device => new SearchHit(
                SearchResultType.Device,
                device.Id,
                device.Address,
                null,
                "Polled by " + device.PollerGroup,
                // Whether anybody is polling it, which is the difference between a device that is quiet and
                // one nothing is watching. Not a severity: what is wrong with it is the alert group's answer.
                device.IsEnabled ? "Enabled" : "Disabled"))
            .ToListAsync(cancellationToken);

        return new SearchSourceResult(hits, total);
    }
}
