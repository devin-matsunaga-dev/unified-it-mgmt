using Modules.Helpdesk.Data;
using Modules.Helpdesk.Features.Tickets;

namespace Modules.Helpdesk.Features.Assignments;

public sealed record CreateTeamRequest(string Name);
public sealed record AddTeamMemberRequest(string TechnicianId);
public sealed record CreateQueueRequest(string Name, Guid TeamId);
public sealed record AssignTicketRequest(string TechnicianId);
public sealed record PlaceTicketInQueueRequest(Guid QueueId);
public sealed record TeamResponse(Guid Id, string Name);
public sealed record QueueResponse(Guid Id, string Name, Guid TeamId);
public sealed record EligibleTechnicianResponse(string Id);
public sealed record TicketAssignmentResponse(
    Guid Id,
    Guid TicketId,
    Guid QueueId,
    string? FromTechnicianId,
    string ToTechnicianId,
    AssignmentKind Kind,
    string ActorId,
    DateTimeOffset OccurredAt);

public enum AssignmentOutcome
{
    Success,
    TicketNotFound,
    TicketHasNoQueue,
    TechnicianNotInQueueTeam,
}

public enum QueuePlacementOutcome
{
    Success,
    TicketNotFound,
    QueueNotFound,
}

public sealed record QueuePlacementResult(
    QueuePlacementOutcome Outcome,
    TicketResponse? Ticket = null);

public sealed record AssignmentResult(
    AssignmentOutcome Outcome,
    TicketResponse? Ticket = null,
    string? Error = null);
