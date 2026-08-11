using Microsoft.Extensions.Logging;

using Quartz;

namespace Platform.Notifications;

/// <summary>
/// Releases what quiet hours withheld. The interval is the granularity of "when quiet hours end", not
/// a deadline: a digest is sent on the first pass after the window closes, so a five-minute trigger
/// puts a 07:00 digest somewhere in 07:00–07:05.
/// </summary>
[DisallowConcurrentExecution]
public sealed class NotificationDigestJob(
    INotificationDigestService digestService,
    ILogger<NotificationDigestJob> logger) : IJob
{
    public async Task Execute(IJobExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            var report = await digestService.RunAsync(DateTimeOffset.UtcNow, context.CancellationToken);
            if (report.Groups > 0 || report.Failed > 0)
            {
                logger.LogInformation(
                    "Notification digest sent {Groups} roll-up(s) covering {Notifications} held notification(s); {Failed} failed.",
                    report.Groups, report.Notifications, report.Failed);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A failed pass leaves every contributor Deferred, so the next one picks them up. Throwing
            // would only put the same work back on Quartz's misfire path.
            logger.LogError(exception, "The notification digest pass failed; held notifications remain held.");
        }
    }
}
