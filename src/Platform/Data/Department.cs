namespace Platform.Data;

public sealed class Department
{
    public Guid Id { get; init; }

    public required string Code { get; init; }

    public required string Name { get; init; }
}