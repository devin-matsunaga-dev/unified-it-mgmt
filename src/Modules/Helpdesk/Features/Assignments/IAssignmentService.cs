using System.Security.Claims;

using Modules.Helpdesk.Features.Tickets;

namespace Modules.Helpdesk.Features.Assignments;

public interface IAssignmentService
{
    Task<TeamResponse> CreateTeamAsync(CreateTeamRequest request, ClaimsPrincipal actor, CancellationToken cancellationToken);
    Task<bool> AddTeamMemberAsync(Guid teamId, AddTeamMemberRequest request, ClaimsPrincipal actor, CancellationToken cancellationToken);
    Task<QueueResponse?> CreateQueueAsync(CreateQueueRequest request, ClaimsPrincipal actor, CancellationToken cancellationToken);
    Task<AssignmentResult> AssignAsync(Guid ticketId, AssignTicketRequest request, ClaimsPrincipal actor, CancellationToken cancellationToken);
    Task<IReadOnlyList<TicketAssignmentResponse>?> GetHistoryAsync(Guid ticketId, ClaimsPrincipal actor, CancellationToken cancellationToken);
    Task<TicketPageResponse> ListMineAsync(int page, int pageSize, ClaimsPrincipal actor, CancellationToken cancellationToken);
}
