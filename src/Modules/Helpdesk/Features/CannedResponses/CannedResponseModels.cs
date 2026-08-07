namespace Modules.Helpdesk.Features.CannedResponses;

public sealed record SaveCannedResponseRequest(string Name, string Body);

public sealed record CannedResponseResponse(
    Guid Id,
    string Name,
    string Body,
    string CreatedById,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record RenderCannedResponseRequest(Guid TicketId);

public sealed record RenderedCannedResponse(Guid Id, string Name, string Body);
