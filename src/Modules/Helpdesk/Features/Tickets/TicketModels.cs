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
