namespace Platform.Data;

public sealed class ConsumerDedupeEntry
{
    public required string Key { get; init; }

    public DateTimeOffset ConsumedAt { get; init; }
}
