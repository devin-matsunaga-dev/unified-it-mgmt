namespace Modules.Helpdesk.Data;

public sealed class Team
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public ICollection<TeamMember> Members { get; set; } = [];
}

public sealed class TeamMember
{
    public Guid TeamId { get; set; }
    public Team Team { get; set; } = null!;
    public string TechnicianId { get; set; } = string.Empty;
    public DateTimeOffset AddedAt { get; set; }
}

public sealed class TicketQueue
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public Guid TeamId { get; set; }
    public Team Team { get; set; } = null!;
    public string? LastAssignedTechnicianId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class TicketAssignmentHistory
{
    public Guid Id { get; set; }
    public Guid TicketId { get; set; }
    public Ticket Ticket { get; set; } = null!;
    public Guid QueueId { get; set; }
    public TicketQueue Queue { get; set; } = null!;
    public string? FromTechnicianId { get; set; }
    public string ToTechnicianId { get; set; } = string.Empty;
    public AssignmentKind Kind { get; set; }
    public string ActorId { get; set; } = string.Empty;
    public DateTimeOffset OccurredAt { get; set; }
}

public enum AssignmentKind
{
    Automatic = 1,
    Manual = 2,
}
