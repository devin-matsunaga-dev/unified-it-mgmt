using System.Net.Mail;
using System.Reflection;

using MailKit.Net.Smtp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace Platform.Notifications;

public sealed record NotificationTemplate(string Name, string Subject, string Body);

public sealed record NotificationMessage(
    string Recipient,
    NotificationTemplate Template,
    object Model,
    IReadOnlyDictionary<string, string>? Headers = null);

public interface INotificationService
{
    Task SendAsync(NotificationMessage message, CancellationToken cancellationToken = default);
}

public sealed class SmtpNotificationService(IConfiguration configuration, ILogger<SmtpNotificationService> logger)
    : INotificationService
{
    public async Task SendAsync(NotificationMessage notification, CancellationToken cancellationToken = default)
    {
        if (!configuration.GetValue("Email:Smtp:Enabled", false))
        {
            logger.LogInformation(
                "Notification {TemplateName} queued for {Recipient} with subject {Subject}",
                notification.Template.Name, notification.Recipient, notification.Template.Subject);
            return;
        }

        if (!MailAddress.TryCreate(notification.Recipient, out _))
        {
            logger.LogWarning("Notification {TemplateName} skipped because {Recipient} is not an email address",
                notification.Template.Name, notification.Recipient);
            return;
        }

        var host = configuration["Email:Smtp:Host"]
            ?? throw new InvalidOperationException("Email:Smtp:Host is required when SMTP is enabled.");
        var port = configuration.GetValue("Email:Smtp:Port", 1025);
        var from = configuration["Email:FromAddress"] ?? "helpdesk@it-platform.local";
        var mimeMessage = new MimeMessage();
        mimeMessage.From.Add(MailboxAddress.Parse(from));
        mimeMessage.To.Add(MailboxAddress.Parse(notification.Recipient));
        mimeMessage.Subject = Render(notification.Template.Subject, notification.Model);
        mimeMessage.Body = new TextPart("plain") { Text = Render(notification.Template.Body, notification.Model) };
        if (notification.Headers is not null)
        {
            foreach (var header in notification.Headers)
            {
                if (header.Key.Equals("Message-Id", StringComparison.OrdinalIgnoreCase))
                {
                    mimeMessage.MessageId = header.Value.Trim('<', '>');
                }
                else
                {
                    mimeMessage.Headers[header.Key] = header.Value;
                }
            }
        }

        using var client = new MailKit.Net.Smtp.SmtpClient();
        await client.ConnectAsync(host, port, false, cancellationToken);
        var username = configuration["Email:Smtp:Username"];
        if (!string.IsNullOrWhiteSpace(username))
        {
            var password = configuration["Email:Smtp:Password"]
                ?? throw new InvalidOperationException("Email:Smtp:Password is required when a username is configured.");
            await client.AuthenticateAsync(username, password, cancellationToken);
        }
        await client.SendAsync(mimeMessage, cancellationToken);
        await client.DisconnectAsync(true, cancellationToken);
    }

    private static string Render(string template, object model)
    {
        foreach (var property in model.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            template = template.Replace($"{{{{{property.Name}}}}}", property.GetValue(model)?.ToString(),
                StringComparison.Ordinal);
        }
        return template;
    }
}
