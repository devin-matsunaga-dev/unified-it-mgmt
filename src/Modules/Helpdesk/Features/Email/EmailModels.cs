namespace Modules.Helpdesk.Features.Email;

public sealed record InboundEmailAttachment(string FileName, string ContentType, byte[] Content);

public sealed record InboundEmailMessage(
    string MailboxId,
    string MessageId,
    string Sender,
    string Subject,
    string Body,
    DateTimeOffset ReceivedAt,
    IReadOnlyList<string> References,
    IReadOnlyList<InboundEmailAttachment> Attachments);

public enum EmailIngestionOutcome
{
    CreatedTicket,
    AddedComment,
    Duplicate,
    Rejected,
}

public sealed record EmailIngestionResult(EmailIngestionOutcome Outcome, Guid? TicketId = null, string? Error = null);
