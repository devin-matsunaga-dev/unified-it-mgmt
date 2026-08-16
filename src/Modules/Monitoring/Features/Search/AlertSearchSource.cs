using System.Security.Claims;

using Microsoft.EntityFrameworkCore;

using Modules.Monitoring.Data;

using Platform.Search;

namespace Modules.Monitoring.Features.Search;

/// <summary>
/// Alerts, from Monitoring's own <c>monitoring.alerts</c> (WP-5.4). Matched on what the alert said, the
/// rule that raised it and the metric it watched.
/// </summary>
public sealed class AlertSearchSource(MonitoringDbContext dbContext) : ISearchSource
{
    public SearchResultType Type => SearchResultType.Alert;

    /// <summary>Agents only, matching the alert board.</summary>
    public bool IsVisibleTo(ClaimsPrincipal actor) => SearchVisibility.IsAgent(actor);

    public async Task<SearchSourceResult> SearchAsync(
        SearchQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        // Cleared alerts are searched as well as open ones, deliberately. The board answers "what is broken
        // now"; a search box is where somebody goes to find the alert from last Tuesday that they are being
        // asked about — and a search that could only find open alerts would answer that question with
        // silence. What keeps the open ones on top is the ordering rather than a filter.
        var alerts = dbContext.Alerts.Where(alert =>
            alert.SearchVector.Matches(EF.Functions.ToTsQuery(SearchTerm.Configuration, query.TsQuery)));

        var total = await alerts.CountAsync(cancellationToken);
        var rows = await alerts
            .OrderByDescending(alert => alert.Status == AlertStatus.Open)
            .ThenByDescending(alert =>
                alert.SearchVector.Rank(EF.Functions.ToTsQuery(SearchTerm.Configuration, query.TsQuery)))
            .ThenByDescending(alert => alert.RaisedAt)
            .Take(query.Limit)
            .Select(alert => new
            {
                alert.Id,
                alert.Summary,
                alert.Severity,
                alert.Status,
                Address = alert.Device.Address,
            })
            .ToListAsync(cancellationToken);

        var hits = rows
            .Select(row => new SearchHit(
                SearchResultType.Alert,
                row.Id,
                row.Summary,
                null,
                // Which device it is about, named the way the device group names it. A cleared alert says so
                // here rather than in the badge, because the badge carries the severity an operator triages
                // on and losing that would flatten a Critical and a Warning into one word.
                row.Status == AlertStatus.Cleared ? $"{row.Address} · cleared" : row.Address,
                row.Severity.ToString()))
            .ToList();

        return new SearchSourceResult(hits, total);
    }
}
