using Microsoft.Extensions.Logging;
using Quartz;

namespace Modules.Helpdesk.Features.Email;

[DisallowConcurrentExecution]
public sealed class EmailIngestionJob(
    IEmailMailbox mailbox,
    IEmailIngestionService ingestionService,
    ILogger<EmailIngestionJob> logger) : IJob
{
    public async Task Execute(IJobExecutionContext context)
    {
        var messages = await mailbox.GetUnreadAsync(context.CancellationToken);
        foreach (var message in messages)
        {
            try
            {
                var result = await ingestionService.ProcessAsync(message, context.CancellationToken);
                if (result.Outcome != EmailIngestionOutcome.Rejected)
                {
                    await mailbox.MarkProcessedAsync(message.MailboxId, context.CancellationToken);
                }
                else
                {
                    logger.LogWarning("Inbound message {MessageId} was rejected: {Reason}", message.MessageId, result.Error);
                }
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Inbound message {MessageId} failed; remaining messages will continue", message.MessageId);
            }
        }
    }
}
