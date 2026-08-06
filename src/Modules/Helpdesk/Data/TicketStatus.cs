namespace Modules.Helpdesk.Data;

public sealed class TicketStatus
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int DisplayOrder { get; set; }
    public bool RequiresResolutionNote { get; set; }
}

public static class DefaultTicketStatuses
{
    public static readonly Guid NewId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    public static readonly Guid TriageId = Guid.Parse("10000000-0000-0000-0000-000000000002");
    public static readonly Guid InProgressId = Guid.Parse("10000000-0000-0000-0000-000000000003");
    public static readonly Guid PendingId = Guid.Parse("10000000-0000-0000-0000-000000000004");
    public static readonly Guid ResolvedId = Guid.Parse("10000000-0000-0000-0000-000000000005");
    public static readonly Guid ClosedId = Guid.Parse("10000000-0000-0000-0000-000000000006");
}

public sealed class TicketStatusTransition
{
    public Guid FromStatusId { get; set; }
    public TicketStatus FromStatus { get; set; } = null!;
    public Guid ToStatusId { get; set; }
    public TicketStatus ToStatus { get; set; } = null!;
}

public sealed class TicketTransitionHistory
{
    public Guid Id { get; set; }
    public Guid TicketId { get; set; }
    public Ticket Ticket { get; set; } = null!;
    public Guid FromStatusId { get; set; }
    public TicketStatus FromStatus { get; set; } = null!;
    public Guid ToStatusId { get; set; }
    public TicketStatus ToStatus { get; set; } = null!;
    public string? ResolutionNote { get; set; }
    public string ActorId { get; set; } = string.Empty;
    public DateTimeOffset OccurredAt { get; set; }
}
