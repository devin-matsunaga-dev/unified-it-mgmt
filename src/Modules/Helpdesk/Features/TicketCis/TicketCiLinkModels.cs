namespace Modules.Helpdesk.Features.TicketCis;

public sealed record LinkTicketCiRequest(Guid CiId);

/// <summary>
/// One linked asset as a ticket renders it. Everything from <see cref="CiName"/> down to
/// <see cref="ContractName"/> is read live from the Assets port at request time, never snapshotted —
/// a renamed owner or a renewed warranty reaches every ticket at once (WP-2.4's rule).
/// </summary>
/// <param name="OpenRelatedTickets">
/// The other unfinished tickets about this same CI. WP-3.7's "open related tickets": an agent holding
/// an automated alert ticket needs to know somebody else is already on it.
/// </param>
public sealed record TicketCiLinkResponse(
    Guid Id,
    Guid TicketId,
    Guid CiId,
    string CiName,
    string CiType,
    string? AssetTag,
    string? SerialNumber,
    string LifecycleState,
    bool IsActive,
    string? OwnerName,
    string? SiteName,
    string? DepartmentName,
    DateOnly? WarrantyExpiresAt,
    string? WarrantyStatus,
    int? WarrantyDaysRemaining,
    string? ContractName,
    IReadOnlyList<RelatedTicketResponse> OpenRelatedTickets,
    string LinkedById,
    string LinkedByName,
    DateTimeOffset LinkedAt);

/// <summary>Another open ticket about the same CI, as a row on the linked-asset card.</summary>
public sealed record RelatedTicketResponse(
    Guid TicketId,
    string Number,
    string Title,
    string Status,
    string Priority,
    DateTimeOffset CreatedAt);

public enum TicketCiLinkOutcome
{
    Success,
    TicketNotFound,
    CiNotFound,
    LinkNotFound,
    Duplicate,
    Forbidden,
}

public sealed record TicketCiLinkResult(
    TicketCiLinkOutcome Outcome,
    TicketCiLinkResponse? Link = null,
    string? Error = null);

public sealed record TicketCiLinkListResult(
    TicketCiLinkOutcome Outcome,
    IReadOnlyList<TicketCiLinkResponse>? Links = null);
