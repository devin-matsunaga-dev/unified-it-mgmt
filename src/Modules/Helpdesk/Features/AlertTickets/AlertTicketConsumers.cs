using Contracts.Events;

using MassTransit;

using Microsoft.Extensions.Logging;

using Platform.Messaging;

namespace Modules.Helpdesk.Features.AlertTickets;

/// <summary>
/// WP-3.5 raises the alert; this opens the ticket. It lives in Helpdesk because a consumer belongs to
/// the module that reacts (CONVENTIONS "Events"), and Helpdesk owns tickets — Monitoring is never
/// asked about one and never reads a helpdesk table.
/// <para>
/// Deduped through the Platform helper. It genuinely needs it: the durable dedupe row stops a second
/// <em>ticket</em>, but a redelivered message would otherwise still add a second annotation and count
/// a second occurrence, which is the kind of quiet double-entry that makes an incident history lie.
/// </para>
/// </summary>
public sealed class AlertRaisedConsumer(
    IConsumerIdempotencyService idempotencyService,
    IAlertTicketAutomation automation,
    ILogger<AlertRaisedConsumer> logger) : IConsumer<AlertRaised>
{
    public async Task Consume(ConsumeContext<AlertRaised> context)
    {
        var alert = context.Message;
        var accepted = await idempotencyService.ExecuteOnceAsync(
            $"alert-ticket-raised:{alert.EventId}",
            cancellationToken => automation.RaiseAsync(alert, cancellationToken),
            context.CancellationToken);

        if (!accepted)
        {
            logger.LogDebug(
                "AlertRaised {EventId} for rule {RuleId} was already handled; skipped.",
                alert.EventId, alert.RuleId);
        }
    }
}

/// <summary>
/// The other half: a cleared alert resolves the ticket it opened, with a note saying why. Nothing
/// closes the ticket — a requester or an agent confirms a resolution, which is the WP-1.8 rule and
/// is not something an automation gets to assume.
/// </summary>
public sealed class AlertClearedConsumer(
    IConsumerIdempotencyService idempotencyService,
    IAlertTicketAutomation automation,
    ILogger<AlertClearedConsumer> logger) : IConsumer<AlertCleared>
{
    public async Task Consume(ConsumeContext<AlertCleared> context)
    {
        var alert = context.Message;
        var accepted = await idempotencyService.ExecuteOnceAsync(
            $"alert-ticket-cleared:{alert.EventId}",
            cancellationToken => automation.ClearAsync(alert, cancellationToken),
            context.CancellationToken);

        if (!accepted)
        {
            logger.LogDebug(
                "AlertCleared {EventId} for rule {RuleId} was already handled; skipped.",
                alert.EventId, alert.RuleId);
        }
    }
}
