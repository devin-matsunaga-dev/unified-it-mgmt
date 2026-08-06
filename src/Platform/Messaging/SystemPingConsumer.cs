using System.Security.Claims;

using Contracts.Events;

using MassTransit;

using Platform.Auditing;

namespace Platform.Messaging;

public sealed class SystemPingConsumer(
    IConsumerIdempotencyService idempotencyService,
    IAuditService auditService) : IConsumer<SystemPing>
{
    private static readonly ClaimsPrincipal SystemActor = new(new ClaimsIdentity(
        [new Claim("sub", "system:message-bus")],
        "MessageBus"));

    public async Task Consume(ConsumeContext<SystemPing> context)
    {
        await idempotencyService.ExecuteOnceAsync(
            $"system-ping:{context.Message.DedupeKey}",
            cancellationToken => auditService.WriteAsync(
                SystemActor,
                "Received",
                nameof(SystemPing),
                context.Message.EventId.ToString(),
                before: null,
                after: new { context.Message.DedupeKey },
                cancellationToken),
            context.CancellationToken);
    }
}
