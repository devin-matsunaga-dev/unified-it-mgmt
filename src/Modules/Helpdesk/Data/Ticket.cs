namespace Modules.Helpdesk.Data;

public sealed class Ticket
{
    public Guid Id { get; set; }
    public long SequenceNumber { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public TicketType Type { get; set; }
    public TicketLevel Urgency { get; set; }
    public TicketLevel Impact { get; set; }
    public TicketPriority Priority { get; set; }
    public Guid StatusId { get; set; }
    public TicketStatus Status { get; set; } = null!;
    public string RequesterId { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public string Number => $"INC-{SequenceNumber:000000}";
}

public enum TicketType
{
    Incident = 1,
    ServiceRequest = 2,
}

public enum TicketLevel
{
    Low = 1,
    Medium = 2,
    High = 3,
}

public enum TicketPriority
{
    Low = 1,
    Medium = 2,
    High = 3,
    Critical = 4,
}
