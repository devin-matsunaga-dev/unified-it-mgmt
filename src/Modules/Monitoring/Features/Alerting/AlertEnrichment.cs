using Microsoft.Extensions.Logging;

using Platform.Integration;

namespace Modules.Monitoring.Features.Alerting;

/// <summary>
/// The CMDB context an alert carries (WP-3.7): who holds the asset, where it is, whether it is still
/// under warranty, and what is already being worked on for it.
/// </summary>
/// <param name="CiFound">
/// False when the CI has been deleted out from under the monitored device — which nothing prevents
/// (see the WP-3.1 note about the missing <c>IMonitoredDeviceDirectory</c> port). Distinguished from
/// "found, but nobody holds it", because those are different facts about the estate.
/// </param>
public sealed record AlertCmdbContext(
    Guid CiId,
    bool CiFound,
    string? CiName,
    string? CiType,
    string? AssetTag,
    string? LifecycleState,
    string? OwnerName,
    string? SiteName,
    string? DepartmentName,
    DateOnly? WarrantyExpiresAt,
    string? WarrantyStatus,
    int? WarrantyDaysRemaining,
    string? ContractName,
    IReadOnlyList<LinkedTicketSummary> OpenTickets)
{
    public static AlertCmdbContext NotFound(Guid ciId) =>
        new(ciId, false, null, null, null, null, null, null, null, null, null, null, null, []);

    /// <summary>One line for a log entry: the thing an operator reads next to "Critical raised".</summary>
    public string Headline => CiFound
        ? $"{CiName} — owner {OwnerName ?? "none"}, location {SiteName ?? "none"}, warranty {WarrantyStatus ?? "none recorded"}, {OpenTickets.Count} open ticket(s)"
        : "CI not found in the CMDB";
}

public interface IAlertEnrichmentService
{
    /// <summary>
    /// The CMDB context for one CI. Read live through the ports on every call and never stored on the
    /// alert row: a renamed owner or a renewed warranty has to reach every alert at once, which is the
    /// same rule WP-2.4 set for ticket links and WP-3.1 for a monitored device's CI name.
    /// </summary>
    Task<AlertCmdbContext> DescribeAsync(Guid ciId, CancellationToken cancellationToken);
}

/// <summary>
/// Monitoring's reader of the two ports. It exists as a service of its own rather than inline in
/// <c>AlertEngine</c> because WP-3.9's alert board needs exactly this and must not re-derive it.
/// </summary>
public sealed class AlertEnrichmentService(
    ICiDirectory ciDirectory,
    ITicketLinkDirectory ticketLinkDirectory,
    ILogger<AlertEnrichmentService> logger) : IAlertEnrichmentService
{
    /// <summary>Enough to say "somebody is already on this" without listing a queue.</summary>
    public const int OpenTicketLimit = 5;

    public async Task<AlertCmdbContext> DescribeAsync(Guid ciId, CancellationToken cancellationToken)
    {
        if (ciId == Guid.Empty)
        {
            return AlertCmdbContext.NotFound(ciId);
        }

        var ci = (await ciDirectory.GetSummariesAsync([ciId], cancellationToken)).SingleOrDefault();
        if (ci is null)
        {
            logger.LogWarning(
                "Alert enrichment found no CI {CiId}; the device it monitors outlived its CMDB record.", ciId);
            return AlertCmdbContext.NotFound(ciId);
        }

        var open = await ticketLinkDirectory.GetOpenTicketsForCiAsync(ciId, OpenTicketLimit, cancellationToken);
        return new AlertCmdbContext(
            ciId,
            CiFound: true,
            ci.Name,
            ci.Type,
            ci.AssetTag,
            ci.LifecycleState,
            ci.OwnerName,
            ci.SiteName,
            ci.DepartmentName,
            ci.WarrantyExpiresAt,
            ci.WarrantyStatus,
            ci.WarrantyDaysRemaining,
            ci.ContractName,
            open);
    }
}
