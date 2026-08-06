namespace Modules.Helpdesk.Features.Email;

public interface IEmailMailbox
{
    Task<IReadOnlyList<InboundEmailMessage>> GetUnreadAsync(CancellationToken cancellationToken);
    Task MarkProcessedAsync(string mailboxId, CancellationToken cancellationToken);
}
