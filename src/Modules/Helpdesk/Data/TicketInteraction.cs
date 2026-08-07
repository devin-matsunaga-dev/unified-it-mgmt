using NpgsqlTypes;

namespace Modules.Helpdesk.Data;

public sealed class TicketComment
{
    public Guid Id { get; set; }
    public Guid TicketId { get; set; }
    public Ticket Ticket { get; set; } = null!;
    public string Body { get; set; } = string.Empty;
    public bool IsInternal { get; set; }
    public string AuthorId { get; set; } = string.Empty;
    public string? AuthorDisplayName { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Database-generated full-text index over the comment body.</summary>
    public NpgsqlTsVector SearchVector { get; set; } = null!;
}

public sealed class TicketWorklog
{
    public Guid Id { get; set; }
    public Guid TicketId { get; set; }
    public Ticket Ticket { get; set; } = null!;
    public int Minutes { get; set; }
    public string? Note { get; set; }
    public string AuthorId { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class TicketAttachment
{
    public Guid Id { get; set; }
    public Guid TicketId { get; set; }
    public Ticket Ticket { get; set; } = null!;
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long Size { get; set; }
    public string ObjectKey { get; set; } = string.Empty;
    public string UploadedById { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}
