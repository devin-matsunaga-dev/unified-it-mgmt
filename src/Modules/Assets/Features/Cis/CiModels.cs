using Modules.Assets.Data;
using Modules.Assets.Features.Contracts;

namespace Modules.Assets.Features.Cis;

public sealed record CreateCiRequest(
    CiType Type,
    string Name,
    string? AssetTag = null,
    string? SerialNumber = null,
    string? Description = null,
    IReadOnlyDictionary<string, string?>? Attributes = null,
    IReadOnlyDictionary<string, string?>? CustomFields = null,
    // A CI is registered either on order or once it reaches the store room; every later state has to
    // be reached through a guarded transition.
    CiLifecycleState LifecycleState = CiLifecycleState.InStock);

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
    CiLifecycleState? LifecycleState = null,
    Guid? OwnerUserId = null,
    Guid? DepartmentId = null,
    Guid? SiteId = null,
    Guid? ContractId = null,
    int? WarrantyExpiringWithinDays = null,
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
    CiLifecycleState LifecycleState,
    CiOwnership Ownership,
    CiCoverage Coverage,
    IReadOnlyDictionary<string, string> Attributes,
    IReadOnlyList<CiCustomFieldValueResponse> CustomFields,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>Who holds a CI and where it lives. Names are snapshots taken when it was assigned.</summary>
public sealed record CiOwnership(
    Guid? OwnerUserId,
    string? OwnerName,
    Guid? DepartmentId,
    string? DepartmentName,
    Guid? SiteId,
    string? SiteName,
    DateTimeOffset? AssignedAt);

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
    IllegalTransition,
    UnknownAssignee,
    Disposed,
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
