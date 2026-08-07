namespace Modules.Helpdesk.Data;

/// <summary>A named ticket-list filter, owned by one agent and optionally shared with the whole team.</summary>
public sealed class TicketView
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string OwnerId { get; set; } = string.Empty;
    public string? OwnerDisplayName { get; set; }
    public bool IsShared { get; set; }
    public string FilterJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
