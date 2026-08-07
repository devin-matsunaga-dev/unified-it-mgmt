namespace Modules.Helpdesk.Data;

/// <summary>
/// A ticket's reference to a configuration item. Helpdesk owns the link and stores nothing but the CI's
/// id — the CI itself is read through the Assets port, so a card never shows a stale name or state.
/// </summary>
public sealed class TicketCiLink
{
    public Guid Id { get; set; }

    public Guid TicketId { get; set; }
    public Ticket Ticket { get; set; } = null!;

    /// <summary>An <c>assets.cis</c> id. Deliberately not a foreign key: schemas do not join.</summary>
    public Guid CiId { get; set; }

    public string LinkedById { get; set; } = string.Empty;
    public string LinkedByName { get; set; } = string.Empty;
    public DateTimeOffset LinkedAt { get; set; }
}
