namespace Modules.Assets.Data;

/// <summary>
/// A user-defined field attached to one CI type. Distinct from the type-specific attributes on the
/// <see cref="ConfigurationItem"/> subclasses: those are fixed columns, these are added at runtime.
/// </summary>
public sealed class CiCustomField
{
    public Guid Id { get; set; }
    public CiType CiType { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public CiCustomFieldType Type { get; set; }
    public bool IsRequired { get; set; }
    public List<string> Options { get; set; } = [];
    public int SortOrder { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class CiCustomFieldValue
{
    public Guid Id { get; set; }
    public Guid CiId { get; set; }
    public ConfigurationItem Ci { get; set; } = null!;
    public Guid FieldId { get; set; }
    public CiCustomField Field { get; set; } = null!;
    public string Value { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; }
}

public enum CiCustomFieldType
{
    Text = 1,
    Number = 2,
    Date = 3,
    Select = 4,
}
