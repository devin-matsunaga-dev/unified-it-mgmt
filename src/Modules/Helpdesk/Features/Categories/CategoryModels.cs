using Modules.Helpdesk.Data;

namespace Modules.Helpdesk.Features.Categories;

public sealed record CreateTicketCategoryRequest(string Name, Guid? ParentId, int SortOrder = 0);

public sealed record UpdateTicketCategoryRequest(string Name, Guid? ParentId, bool IsActive, int SortOrder = 0);

public sealed record CreateCustomFieldRequest(
    string Key,
    string Label,
    CustomFieldType Type,
    bool IsRequired,
    IReadOnlyList<string>? Options = null,
    int SortOrder = 0);

public sealed record CustomFieldResponse(
    Guid Id,
    Guid CategoryId,
    string Key,
    string Label,
    CustomFieldType Type,
    bool IsRequired,
    IReadOnlyList<string> Options,
    int SortOrder);

public sealed record TicketCategoryResponse(
    Guid Id,
    string Name,
    Guid? ParentId,
    bool IsActive,
    int SortOrder,
    IReadOnlyList<CustomFieldResponse> Fields,
    IReadOnlyList<TicketCategoryResponse> Children);

public sealed record TicketCustomFieldValueResponse(
    Guid FieldId,
    string Key,
    string Label,
    CustomFieldType Type,
    string Value);

public enum CategoryOutcome
{
    Success,
    NotFound,
    ParentNotFound,
    CycleDetected,
    DuplicateName,
    InUse,
}

public sealed record CategoryResult(
    CategoryOutcome Outcome,
    TicketCategoryResponse? Category = null,
    string? Error = null);

public sealed record CustomFieldResult(
    CategoryOutcome Outcome,
    CustomFieldResponse? Field = null,
    string? Error = null);
