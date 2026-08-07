namespace Modules.Helpdesk.Data;

public sealed class TicketCategory
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public Guid? ParentId { get; set; }
    public TicketCategory? Parent { get; set; }
    public ICollection<TicketCategory> Children { get; set; } = [];
    public ICollection<TicketCustomField> Fields { get; set; } = [];
    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class TicketCustomField
{
    public Guid Id { get; set; }
    public Guid CategoryId { get; set; }
    public TicketCategory Category { get; set; } = null!;
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public CustomFieldType Type { get; set; }
    public bool IsRequired { get; set; }
    public List<string> Options { get; set; } = [];
    public int SortOrder { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class TicketCustomFieldValue
{
    public Guid Id { get; set; }
    public Guid TicketId { get; set; }
    public Ticket Ticket { get; set; } = null!;
    public Guid FieldId { get; set; }
    public TicketCustomField Field { get; set; } = null!;
    public string Value { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; }
}

public enum CustomFieldType
{
    Text = 1,
    Number = 2,
    Date = 3,
    Select = 4,
}
