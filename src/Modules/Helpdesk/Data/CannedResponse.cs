namespace Modules.Helpdesk.Data;

/// <summary>A reusable reply body with placeholders resolved against the ticket it is inserted into.</summary>
public sealed class CannedResponse
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string CreatedById { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
