namespace Modules.Helpdesk.Features.TicketCis;

public sealed record LinkTicketCiRequest(Guid CiId);

/// <summary>
/// One linked asset as a ticket renders it. Everything from <see cref="CiName"/> down is read live from
/// the Assets port at request time, never snapshotted.
/// </summary>
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
    string LinkedById,
    string LinkedByName,
    DateTimeOffset LinkedAt);

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
