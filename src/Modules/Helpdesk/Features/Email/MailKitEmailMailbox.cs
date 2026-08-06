using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using Microsoft.Extensions.Configuration;
using MimeKit;

namespace Modules.Helpdesk.Features.Email;

public sealed class MailKitEmailMailbox(IConfiguration configuration) : IEmailMailbox
{
    public async Task<IReadOnlyList<InboundEmailMessage>> GetUnreadAsync(CancellationToken cancellationToken)
    {
        if (!configuration.GetValue("Email:Imap:Enabled", false))
        {
            return [];
        }

        using var client = await ConnectAsync(cancellationToken);
        var inbox = client.Inbox;
        await inbox.OpenAsync(FolderAccess.ReadWrite, cancellationToken);
        var ids = await inbox.SearchAsync(SearchQuery.NotSeen, cancellationToken);
        var messages = new List<InboundEmailMessage>(ids.Count);
        foreach (var id in ids)
        {
            var message = await inbox.GetMessageAsync(id, cancellationToken);
            var sender = message.From.Mailboxes.FirstOrDefault()?.Address ?? string.Empty;
            var references = message.References.ToList();
            if (!string.IsNullOrWhiteSpace(message.InReplyTo))
            {
                references.Add(message.InReplyTo);
            }
            var attachments = new List<InboundEmailAttachment>();
            foreach (var part in message.Attachments.OfType<MimePart>())
            {
                if (part.Content is null)
                {
                    continue;
                }
                await using var content = new MemoryStream();
                await part.Content.DecodeToAsync(content, cancellationToken);
                attachments.Add(new InboundEmailAttachment(
                    part.FileName ?? "attachment", part.ContentType.MimeType, content.ToArray()));
            }
            messages.Add(new InboundEmailMessage(
                id.Id.ToString(), message.MessageId ?? string.Empty, sender, message.Subject ?? "(no subject)",
                message.TextBody ?? message.HtmlBody ?? string.Empty, message.Date, references, attachments));
        }
        await client.DisconnectAsync(true, cancellationToken);
        return messages;
    }

    public async Task MarkProcessedAsync(string mailboxId, CancellationToken cancellationToken)
    {
        using var client = await ConnectAsync(cancellationToken);
        var inbox = client.Inbox;
        await inbox.OpenAsync(FolderAccess.ReadWrite, cancellationToken);
        await inbox.AddFlagsAsync(new UniqueId(uint.Parse(mailboxId, System.Globalization.CultureInfo.InvariantCulture)),
            MessageFlags.Seen, true, cancellationToken);
        await client.DisconnectAsync(true, cancellationToken);
    }

    private async Task<ImapClient> ConnectAsync(CancellationToken cancellationToken)
    {
        var host = configuration["Email:Imap:Host"]
            ?? throw new InvalidOperationException("Email:Imap:Host is required when IMAP is enabled.");
        var client = new ImapClient();
        await client.ConnectAsync(host, configuration.GetValue("Email:Imap:Port", 143),
            configuration.GetValue("Email:Imap:UseSsl", false), cancellationToken);
        await client.AuthenticateAsync(
            configuration["Email:Imap:Username"] ?? throw new InvalidOperationException("Email:Imap:Username is required."),
            configuration["Email:Imap:Password"] ?? throw new InvalidOperationException("Email:Imap:Password is required."),
            cancellationToken);
        return client;
    }
}
