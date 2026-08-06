using System.Net.Mail;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Modules.Helpdesk.Data;
using Modules.Helpdesk.Features.Interactions;
using Modules.Helpdesk.Features.Tickets;
using Platform.Auditing;

namespace Modules.Helpdesk.Features.Email;

public interface IEmailIngestionService
{
    Task<EmailIngestionResult> ProcessAsync(InboundEmailMessage message, CancellationToken cancellationToken);
}

public sealed partial class EmailIngestionService(
    HelpdeskDbContext dbContext,
    ITicketService ticketService,
    IInteractionService interactionService,
    IAuditService auditService,
    ILogger<EmailIngestionService> logger) : IEmailIngestionService
{
    public async Task<EmailIngestionResult> ProcessAsync(
        InboundEmailMessage message, CancellationToken cancellationToken)
    {
        if (!MailAddress.TryCreate(message.Sender, out _) || message.Sender.Length > 320
            || string.IsNullOrWhiteSpace(message.Body))
        {
            return new(EmailIngestionOutcome.Rejected, Error: "A valid sender and non-empty message body are required.");
        }
        var messageId = NormalizeMessageId(message);
        if (messageId.Length > 998)
        {
            return new(EmailIngestionOutcome.Rejected, Error: "The email Message-ID exceeds the supported length.");
        }
        if (await dbContext.TicketEmails.AnyAsync(email => email.MessageId == messageId, cancellationToken))
        {
            return new(EmailIngestionOutcome.Duplicate);
        }

        var actor = EmailActor(message.Sender);
        var ticket = await ResolveTicketAsync(message, cancellationToken);
        EmailIngestionOutcome outcome;
        if (ticket is null)
        {
            var created = await ticketService.CreateAsync(new CreateTicketRequest(
                CleanSubject(message.Subject), message.Body[..Math.Min(message.Body.Length, 10_000)],
                TicketType.Incident, TicketLevel.Medium, TicketLevel.Medium, message.Sender, null),
                actor, cancellationToken);
            if (created is null)
            {
                return new(EmailIngestionOutcome.Rejected, Error: "The ticket could not be created.");
            }
            ticket = await dbContext.Tickets.SingleAsync(item => item.Id == created.Id, cancellationToken);
            outcome = EmailIngestionOutcome.CreatedTicket;
        }
        else
        {
            var commentBody = message.Body[..Math.Min(message.Body.Length, 10_000)];
            var comment = await interactionService.AddCommentAsync(
                ticket.Id, new CreateCommentRequest(commentBody, false), actor, cancellationToken);
            if (comment.Outcome != InteractionOutcome.Success)
            {
                return new(EmailIngestionOutcome.Rejected, ticket.Id, comment.Error);
            }
            outcome = EmailIngestionOutcome.AddedComment;
        }

        foreach (var item in message.Attachments)
        {
            await using var content = new MemoryStream(item.Content, writable: false);
            var file = new FormFile(content, 0, item.Content.LongLength, "attachment", item.FileName)
            {
                Headers = new HeaderDictionary(), ContentType = item.ContentType,
            };
            var result = await interactionService.AddAttachmentAsync(ticket.Id, file, actor, cancellationToken);
            if (result.Outcome != InteractionOutcome.Success)
            {
                logger.LogWarning("Email attachment {FileName} was rejected for ticket {TicketId}: {Reason}",
                    item.FileName, ticket.Id, result.Error);
            }
        }

        var email = new TicketEmail
        {
            Id = Guid.CreateVersion7(), TicketId = ticket.Id, MessageId = messageId,
            Sender = message.Sender,
            Subject = message.Subject[..Math.Min(message.Subject.Length, 998)],
            ReceivedAt = message.ReceivedAt,
        };
        dbContext.TicketEmails.Add(email);
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditService.WriteAsync(actor, "Received", "TicketEmail", email.Id.ToString(), null,
            new { email.TicketId, email.MessageId, email.Sender, email.Subject }, cancellationToken);
        return new(outcome, ticket.Id);
    }

    private async Task<Ticket?> ResolveTicketAsync(InboundEmailMessage message, CancellationToken cancellationToken)
    {
        foreach (var reference in message.References)
        {
            var ticketMatch = TicketMessageIdRegex().Match(reference);
            if (ticketMatch.Success && Guid.TryParseExact(ticketMatch.Groups[1].Value, "N", out var ticketId))
            {
                var ticket = await dbContext.Tickets.SingleOrDefaultAsync(item => item.Id == ticketId, cancellationToken);
                if (ticket is not null) return ticket;
            }
            var email = await dbContext.TicketEmails.Include(item => item.Ticket)
                .SingleOrDefaultAsync(item => item.MessageId == reference.Trim('<', '>'), cancellationToken);
            if (email is not null) return email.Ticket;
        }
        var subjectMatch = TicketNumberRegex().Match(message.Subject);
        return subjectMatch.Success && long.TryParse(subjectMatch.Groups[1].Value, out var sequence)
            ? await dbContext.Tickets.SingleOrDefaultAsync(item => item.SequenceNumber == sequence, cancellationToken)
            : null;
    }

    private static string NormalizeMessageId(InboundEmailMessage message)
    {
        if (!string.IsNullOrWhiteSpace(message.MessageId)) return message.MessageId.Trim('<', '>');
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{message.Sender}\n{message.ReceivedAt:O}\n{message.Subject}\n{message.Body}"));
        return $"synthetic-{Convert.ToHexString(bytes).ToLowerInvariant()}";
    }

    private static ClaimsPrincipal EmailActor(string sender) => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.NameIdentifier, sender), new Claim(ClaimTypes.Role, "EndUser")], "Email"));

    private static string CleanSubject(string subject)
    {
        var result = ReplyPrefixRegex().Replace(subject, string.Empty).Trim();
        result = string.IsNullOrWhiteSpace(result) ? "Email request" : result;
        return result[..Math.Min(result.Length, 200)];
    }

    [GeneratedRegex(@"ticket-([0-9a-fA-F]{32})@it-platform\.local", RegexOptions.IgnoreCase)]
    private static partial Regex TicketMessageIdRegex();
    [GeneratedRegex(@"\[INC-(\d{6,})\]", RegexOptions.IgnoreCase)]
    private static partial Regex TicketNumberRegex();
    [GeneratedRegex(@"^(?:(?:re|fw|fwd):\s*)+", RegexOptions.IgnoreCase)]
    private static partial Regex ReplyPrefixRegex();
}
