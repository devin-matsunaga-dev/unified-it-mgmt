using System.Security.Claims;
using Modules.Helpdesk.Data;

namespace Modules.Helpdesk.Features.Sla;

public interface ISlaService
{
    Task<BusinessHoursCalendarResponse> CreateCalendarAsync(CreateBusinessHoursCalendarRequest request, ClaimsPrincipal actor, CancellationToken cancellationToken);
    Task<SlaPolicyResponse?> CreatePolicyAsync(CreateSlaPolicyRequest request, ClaimsPrincipal actor, CancellationToken cancellationToken);
    Task<SlaRemainingResponse?> GetRemainingAsync(Guid ticketId, ClaimsPrincipal actor, CancellationToken cancellationToken);
    Task StartAsync(Ticket ticket, DateTimeOffset now, CancellationToken cancellationToken);
    Task RecordStatusChangeAsync(Ticket ticket, Guid fromStatusId, DateTimeOffset now, CancellationToken cancellationToken);
    Task MarkRespondedAsync(Guid ticketId, DateTimeOffset now, CancellationToken cancellationToken);
    Task EvaluateAsync(DateTimeOffset now, CancellationToken cancellationToken);
}
