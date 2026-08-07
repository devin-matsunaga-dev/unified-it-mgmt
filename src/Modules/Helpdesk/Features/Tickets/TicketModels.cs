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
    IReadOnlyDictionary<string, string?>? CustomFields = null);

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
    IReadOnlyList<TicketCustomFieldValueResponse>? CustomFields = null);

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
