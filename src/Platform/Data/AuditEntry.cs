namespace Platform.Data;

public sealed class AuditEntry
{
    public Guid Id { get; init; }

    public required string ActorId { get; init; }

    public required string Action { get; init; }

    public required string EntityType { get; init; }

    public required string EntityId { get; init; }

    public string? BeforeJson { get; init; }

    public string? AfterJson { get; init; }

    public DateTimeOffset OccurredAt { get; init; }

    public required string CorrelationId { get; init; }
}