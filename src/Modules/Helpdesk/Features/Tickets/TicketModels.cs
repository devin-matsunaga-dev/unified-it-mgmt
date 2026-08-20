using Modules.Helpdesk.Data;
using Modules.Helpdesk.Features.Categories;

namespace Modules.Helpdesk.Features.Tickets;

public sealed record CreateTicketRequest(
    string Title,
    string Description,
    TicketType Type,
    TicketLevel Urgency,
    TicketLevel Impact,
    string? RequesterId,
    Guid? QueueId,
    Guid? CategoryId = null,
    IReadOnlyDictionary<string, string?>? CustomFields = null,
    /// <summary>
    /// CIs to link as the ticket is created, in the same transaction. A field technician raising a
    /// ticket about the asset in their hand would otherwise need a second call, and on the kind of
    /// connection a loading dock has, the second call is the one that fails — leaving a ticket that
    /// names no asset and a technician who believes it does.
    /// </summary>
    IReadOnlyList<Guid>? CiIds = null);

public sealed record UpdateTicketRequest(
    string Title,
    string Description,
    TicketType Type,
    TicketLevel Urgency,
    TicketLevel Impact,
    Guid? CategoryId = null,
    IReadOnlyDictionary<string, string?>? CustomFields = null);

public sealed record TicketResponse(
    Guid Id,
    string Number,
    string Title,
    string Description,
    TicketType Type,
    TicketLevel Urgency,
    TicketLevel Impact,
    TicketPriority Priority,
    string Status,
    string RequesterId,
    string RequesterName,
    Guid? QueueId,
    string? QueueName,
    string? AssignedTechnicianId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    Guid? CategoryId = null,
    string? CategoryName = null,
    IReadOnlyList<TicketCustomFieldValueResponse>? CustomFields = null,
    /// <summary>
    /// The requester's department and location, resolved through Platform's directory at read time
    /// rather than stored on the ticket. Derived, so it is always the truth about where the person
    /// sits and there is nothing to keep in sync; null when the requester is not a directory user,
    /// which an alert-raised or an emailed-in ticket often is not.
    /// </summary>
    string? RequesterDepartmentName = null,
    string? RequesterSiteName = null);

/// <summary>
/// The ticket list filter. Doubles as the persisted payload of a saved view, so every member has to be
/// optional and serialisable on its own.
/// </summary>
public sealed record TicketListFilter(
    string? Search = null,
    IReadOnlyList<string>? Statuses = null,
    IReadOnlyList<TicketPriority>? Priorities = null,
    TicketType? Type = null,
    Guid? QueueId = null,
    string? AssignedTechnicianId = null,
    Guid? CategoryId = null,
    bool Unassigned = false,
    // The 360° pages: every ticket about one CI, and every ticket raised by one person.
    Guid? CiId = null,
    string? RequesterId = null)
{
    public static readonly TicketListFilter Empty = new();
}

public sealed record TicketListResult(
    TicketPageResponse? Page,
    IReadOnlyDictionary<string, string[]>? Errors = null);

public sealed record TicketPageResponse(
    IReadOnlyList<TicketResponse> Items,
    int Total,
    int Page,
    int PageSize);

public sealed record TransitionTicketRequest(string TargetStatus, string? ResolutionNote);

public sealed record TicketTransitionResponse(
    Guid Id,
    Guid TicketId,
    string FromStatus,
    string ToStatus,
    string? ResolutionNote,
    string ActorId,
    DateTimeOffset OccurredAt);
