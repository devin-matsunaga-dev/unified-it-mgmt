using System.Security.Claims;

namespace Modules.Helpdesk.Features.Categories;

public interface ICategoryService
{
    Task<IReadOnlyList<TicketCategoryResponse>> GetTreeAsync(bool includeInactive, CancellationToken cancellationToken);

    Task<CategoryResult> CreateAsync(
        CreateTicketCategoryRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken);

    Task<CategoryResult> UpdateAsync(
        Guid id,
        UpdateTicketCategoryRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken);

    Task<CategoryOutcome> DeleteAsync(Guid id, ClaimsPrincipal actor, CancellationToken cancellationToken);

    Task<CustomFieldResult> AddFieldAsync(
        Guid categoryId,
        CreateCustomFieldRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken);

    Task<CategoryOutcome> DeleteFieldAsync(
        Guid categoryId,
        Guid fieldId,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken);
}
