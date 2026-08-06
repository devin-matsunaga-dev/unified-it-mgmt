namespace Platform.Data;

public sealed class UserProfile
{
    public Guid Id { get; init; }

    public required string Username { get; init; }

    public required string Email { get; init; }

    public required string DisplayName { get; init; }

    public required string Role { get; init; }

    public Guid SiteId { get; init; }

    public Site? Site { get; init; }

    public Guid DepartmentId { get; init; }

    public Department? Department { get; init; }
}