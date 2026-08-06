using Microsoft.Extensions.Logging;

namespace Platform.Notifications;

public sealed record NotificationTemplate(string Name, string Subject, string Body);

public sealed record NotificationMessage(string Recipient, NotificationTemplate Template, object Model);

public interface INotificationService
{
    Task SendAsync(NotificationMessage message, CancellationToken cancellationToken = default);
}

public sealed class LoggingNotificationService(ILogger<LoggingNotificationService> logger) : INotificationService
{
    public Task SendAsync(NotificationMessage message, CancellationToken cancellationToken = default)
    {
        logger.LogInformation(
            "Notification {TemplateName} queued for {Recipient} with subject {Subject}",
            message.Template.Name,
            message.Recipient,
            message.Template.Subject);
        return Task.CompletedTask;
    }
}