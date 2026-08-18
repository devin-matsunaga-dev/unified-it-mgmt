using System.Security.Claims;
using Modules.Helpdesk.Data;

namespace Modules.Helpdesk.Features.Sla;

public interface ISlaService
{
    Task<BusinessHoursCalendarResponse> CreateCalendarAsync(CreateBusinessHoursCalendarRequest request, ClaimsPrincipal actor, CancellationToken cancellationToken);
    Task<IReadOnlyList<BusinessHoursCalendarResponse>> ListCalendarsAsync(CancellationToken cancellationToken);
    Task<SlaOutcome> DeleteCalendarAsync(Guid id, ClaimsPrincipal actor, CancellationToken cancellationToken);

    Task<IReadOnlyList<SlaPolicyResponse>> ListPoliciesAsync(CancellationToken cancellationToken);
    Task<SlaPolicyResult> CreatePolicyAsync(SavePolicyRequest request, ClaimsPrincipal actor, CancellationToken cancellationToken);
    Task<SlaPolicyResult> UpdatePolicyAsync(Guid id, SavePolicyRequest request, ClaimsPrincipal actor, CancellationToken cancellationToken);
    Task<SlaOutcome> DeletePolicyAsync(Guid id, ClaimsPrincipal actor, CancellationToken cancellationToken);
    Task ReorderPoliciesAsync(IReadOnlyList<Guid> policyIds, ClaimsPrincipal actor, CancellationToken cancellationToken);
    Task<SlaRemainingResponse?> GetRemainingAsync(Guid ticketId, ClaimsPrincipal actor, CancellationToken cancellationToken);
    Task StartAsync(Ticket ticket, DateTimeOffset now, CancellationToken cancellationToken);
    Task RecordStatusChangeAsync(Ticket ticket, Guid fromStatusId, DateTimeOffset now, CancellationToken cancellationToken);
    Task MarkRespondedAsync(Guid ticketId, DateTimeOffset now, CancellationToken cancellationToken);
    Task EvaluateAsync(DateTimeOffset now, CancellationToken cancellationToken);
}
