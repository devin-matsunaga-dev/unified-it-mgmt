using Modules.Assets.Data;

namespace Modules.Assets.Features.Cis;

public sealed record CreateCiRequest(
    CiType Type,
    string Name,
    string? AssetTag = null,
    string? SerialNumber = null,
    string? Description = null,
    IReadOnlyDictionary<string, string?>? Attributes = null,
    IReadOnlyDictionary<string, string?>? CustomFields = null);

public sealed record UpdateCiRequest(
    string Name,
    string? AssetTag = null,
    string? SerialNumber = null,
    string? Description = null,
    bool IsActive = true,
    IReadOnlyDictionary<string, string?>? Attributes = null,
    IReadOnlyDictionary<string, string?>? CustomFields = null);

public sealed record CiListRequest(
    CiType? Type = null,
    string? Search = null,
    bool? IsActive = null,
    int Page = 1,
    int PageSize = 25);

public sealed record CiCustomFieldValueResponse(
    Guid FieldId,
    string Key,
    string Label,
    CiCustomFieldType Type,
    string Value);

public sealed record CiResponse(
    Guid Id,
    CiType Type,
    string Name,
    string? AssetTag,
    string? SerialNumber,
    string? Description,
    bool IsActive,
    IReadOnlyDictionary<string, string> Attributes,
    IReadOnlyList<CiCustomFieldValueResponse> CustomFields,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record CiPageResponse(
    IReadOnlyList<CiResponse> Items,
    int Total,
    int Page,
    int PageSize);

/// <summary>The fixed attributes and runtime custom fields a form must render for one CI type.</summary>
public sealed record CiTypeSchemaResponse(
    CiType Type,
    IReadOnlyList<CiAttributeDefinition> Attributes,
    IReadOnlyList<CiCustomFieldResponse> CustomFields);

public sealed record CreateCiCustomFieldRequest(
    CiType CiType,
    string Key,
    string Label,
    CiCustomFieldType Type,
    bool IsRequired,
    IReadOnlyList<string>? Options = null,
    int SortOrder = 0);

public sealed record CiCustomFieldResponse(
    Guid Id,
    CiType CiType,
    string Key,
    string Label,
    CiCustomFieldType Type,
    bool IsRequired,
    IReadOnlyList<string> Options,
    int SortOrder);

public enum CiOutcome
{
    Success,
    NotFound,
    InvalidAttributes,
    InvalidCustomFields,
    DuplicateIdentifier,
    InUse,
}

public sealed record CiResult(
    CiOutcome Outcome,
    CiResponse? Ci = null,
    IReadOnlyDictionary<string, string[]>? Errors = null,
    string? Error = null);

public sealed record CiCustomFieldResult(
    CiOutcome Outcome,
    CiCustomFieldResponse? Field = null,
    string? Error = null);
