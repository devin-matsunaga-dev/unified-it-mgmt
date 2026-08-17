using Contracts.Events;

using MassTransit;

using Microsoft.Extensions.Logging;

using Platform.Messaging;

namespace Modules.Monitoring.Features.Runbooks;

/// <summary>
/// WP-3.5 raises the alert; this decides whether anything should be done about it automatically.
/// <para>
/// It consumes an event this module itself publishes, which is unusual here and deliberate. The
/// alternative is a call inside the alert engine's own transaction, and that would put "start a
/// remediation on a machine" inside the write that records the alert — so a runbook that failed to be
/// requested could fail the alert, and a redelivery of the engine's work would re-request it. On its
/// own endpoint it is idempotent, retried by MassTransit rather than by the engine, and cannot slow the
/// evaluation path down. It sits beside Helpdesk's ticket consumer on the same event for the same
/// reasons WP-3.10's notification consumers do.
/// </para>
/// </summary>
public sealed class AlertRunbookConsumer(
    IConsumerIdempotencyService idempotencyService,
    IRunbookExecutionService executionService,
    ILogger<AlertRunbookConsumer> logger) : IConsumer<AlertRaised>
{
    public async Task Consume(ConsumeContext<AlertRaised> context)
    {
        var alert = context.Message;
        var accepted = await idempotencyService.ExecuteOnceAsync(
            $"alert-runbook:{alert.EventId}",
            async cancellationToken =>
            {
                var started = await executionService.TriggerAsync(alert, cancellationToken);
                if (started > 0)
                {
                    logger.LogInformation(
                        "Alert {AlertId} started {Count} runbook execution(s).", alert.AlertId, started);
                }
            },
            context.CancellationToken);

        if (!accepted)
        {
            logger.LogDebug(
                "AlertRaised {EventId} for rule {RuleId} was already considered for remediation; skipped.",
                alert.EventId, alert.RuleId);
        }
    }
}
