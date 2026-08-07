using System.Security.Claims;

using Microsoft.EntityFrameworkCore;
using Modules.Helpdesk.Data;
using Platform.Auditing;

namespace Modules.Helpdesk.Features.Categories;

public sealed class CategoryService(HelpdeskDbContext dbContext, IAuditService auditService) : ICategoryService
{
    internal const int MaximumDepth = 3;

    public async Task<IReadOnlyList<TicketCategoryResponse>> GetTreeAsync(
        bool includeInactive,
        CancellationToken cancellationToken)
    {
        var categories = await dbContext.TicketCategories
            .Include(category => category.Fields)
            .Where(category => includeInactive || category.IsActive)
            .OrderBy(category => category.SortOrder).ThenBy(category => category.Name)
            .ToListAsync(cancellationToken);
        return BuildTree(categories, null);
    }

    public async Task<CategoryResult> CreateAsync(
        CreateTicketCategoryRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        var name = request.Name.Trim();
        if (request.ParentId is not null)
        {
            var parent = await dbContext.TicketCategories
                .SingleOrDefaultAsync(item => item.Id == request.ParentId, cancellationToken);
            if (parent is null)
            {
                return new(CategoryOutcome.ParentNotFound, Error: "Parent category not found.");
            }

            if (await DepthAsync(parent, cancellationToken) >= MaximumDepth)
            {
                return new(
                    CategoryOutcome.CycleDetected,
                    Error: $"Categories may not be nested more than {MaximumDepth} levels deep.");
            }
        }

        if (await NameTakenAsync(request.ParentId, name, null, cancellationToken))
        {
            return new(CategoryOutcome.DuplicateName, Error: $"A category named '{name}' already exists here.");
        }

        var category = new TicketCategory
        {
            Id = Guid.CreateVersion7(),
            Name = name,
            ParentId = request.ParentId,
            IsActive = true,
            SortOrder = request.SortOrder,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        dbContext.TicketCategories.Add(category);
        await dbContext.SaveChangesAsync(cancellationToken);

        var response = Map(category, []);
        await auditService.WriteAsync(
            actor, "Created", "TicketCategory", category.Id.ToString(), null, response, cancellationToken);
        return new(CategoryOutcome.Success, response);
    }

    public async Task<CategoryResult> UpdateAsync(
        Guid id,
        UpdateTicketCategoryRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        var category = await dbContext.TicketCategories.Include(item => item.Fields)
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (category is null)
        {
            return new(CategoryOutcome.NotFound);
        }

        var name = request.Name.Trim();
        if (request.ParentId != category.ParentId && request.ParentId is not null)
        {
            var parent = await dbContext.TicketCategories
                .SingleOrDefaultAsync(item => item.Id == request.ParentId, cancellationToken);
            if (parent is null)
            {
                return new(CategoryOutcome.ParentNotFound, Error: "Parent category not found.");
            }

            if (request.ParentId == id || await IsDescendantAsync(parent, id, cancellationToken))
            {
                return new(CategoryOutcome.CycleDetected, Error: "A category cannot be nested inside itself.");
            }

            if (await DepthAsync(parent, cancellationToken) >= MaximumDepth)
            {
                return new(
                    CategoryOutcome.CycleDetected,
                    Error: $"Categories may not be nested more than {MaximumDepth} levels deep.");
            }
        }

        if (await NameTakenAsync(request.ParentId, name, id, cancellationToken))
        {
            return new(CategoryOutcome.DuplicateName, Error: $"A category named '{name}' already exists here.");
        }

        var before = Map(category, category.Fields);
        category.Name = name;
        category.ParentId = request.ParentId;
        category.IsActive = request.IsActive;
        category.SortOrder = request.SortOrder;
        await dbContext.SaveChangesAsync(cancellationToken);

        var after = Map(category, category.Fields);
        await auditService.WriteAsync(
            actor, "Updated", "TicketCategory", category.Id.ToString(), before, after, cancellationToken);
        return new(CategoryOutcome.Success, after);
    }

    public async Task<CategoryOutcome> DeleteAsync(Guid id, ClaimsPrincipal actor, CancellationToken cancellationToken)
    {
        var category = await dbContext.TicketCategories.Include(item => item.Fields)
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (category is null)
        {
            return CategoryOutcome.NotFound;
        }

        if (await dbContext.Tickets.AnyAsync(ticket => ticket.CategoryId == id, cancellationToken)
            || await dbContext.TicketCategories.AnyAsync(item => item.ParentId == id, cancellationToken))
        {
            return CategoryOutcome.InUse;
        }

        var before = Map(category, category.Fields);
        dbContext.TicketCategories.Remove(category);
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditService.WriteAsync(
            actor, "Deleted", "TicketCategory", id.ToString(), before, null, cancellationToken);
        return CategoryOutcome.Success;
    }

    public async Task<CustomFieldResult> AddFieldAsync(
        Guid categoryId,
        CreateCustomFieldRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        var category = await dbContext.TicketCategories
            .SingleOrDefaultAsync(item => item.Id == categoryId, cancellationToken);
        if (category is null)
        {
            return new(CategoryOutcome.NotFound);
        }

        var key = request.Key.Trim();
        if (await dbContext.TicketCustomFields.AnyAsync(
                field => field.CategoryId == categoryId && field.Key.ToLower() == key.ToLower(), cancellationToken))
        {
            return new(CategoryOutcome.DuplicateName, Error: $"A field with key '{key}' already exists on this category.");
        }

        var field = new TicketCustomField
        {
            Id = Guid.CreateVersion7(),
            CategoryId = categoryId,
            Key = key,
            Label = request.Label.Trim(),
            Type = request.Type,
            IsRequired = request.IsRequired,
            Options = request.Type == CustomFieldType.Select
                ? [.. (request.Options ?? []).Select(option => option.Trim())]
                : [],
            SortOrder = request.SortOrder,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        dbContext.TicketCustomFields.Add(field);
        await dbContext.SaveChangesAsync(cancellationToken);

        var response = Map(field);
        await auditService.WriteAsync(
            actor, "Created", "TicketCustomField", field.Id.ToString(), null, response, cancellationToken);
        return new(CategoryOutcome.Success, response);
    }

    public async Task<CategoryOutcome> DeleteFieldAsync(
        Guid categoryId,
        Guid fieldId,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        var field = await dbContext.TicketCustomFields
            .SingleOrDefaultAsync(item => item.Id == fieldId && item.CategoryId == categoryId, cancellationToken);
        if (field is null)
        {
            return CategoryOutcome.NotFound;
        }

        if (await dbContext.TicketCustomFieldValues.AnyAsync(value => value.FieldId == fieldId, cancellationToken))
        {
            return CategoryOutcome.InUse;
        }

        var before = Map(field);
        dbContext.TicketCustomFields.Remove(field);
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditService.WriteAsync(
            actor, "Deleted", "TicketCustomField", fieldId.ToString(), before, null, cancellationToken);
        return CategoryOutcome.Success;
    }

    private async Task<bool> NameTakenAsync(
        Guid? parentId,
        string name,
        Guid? excludingId,
        CancellationToken cancellationToken) =>
        await dbContext.TicketCategories.AnyAsync(
            item => item.ParentId == parentId && item.Name.ToLower() == name.ToLower() && item.Id != excludingId,
            cancellationToken);

    private async Task<int> DepthAsync(TicketCategory category, CancellationToken cancellationToken)
    {
        var depth = 1;
        var parentId = category.ParentId;
        while (parentId is not null && depth <= MaximumDepth)
        {
            parentId = await dbContext.TicketCategories.Where(item => item.Id == parentId)
                .Select(item => item.ParentId).SingleAsync(cancellationToken);
            depth++;
        }

        return depth;
    }

    private async Task<bool> IsDescendantAsync(TicketCategory candidate, Guid ancestorId, CancellationToken cancellationToken)
    {
        var parentId = candidate.ParentId;
        var guard = 0;
        while (parentId is not null && guard++ <= MaximumDepth)
        {
            if (parentId == ancestorId)
            {
                return true;
            }

            parentId = await dbContext.TicketCategories.Where(item => item.Id == parentId)
                .Select(item => item.ParentId).SingleAsync(cancellationToken);
        }

        return false;
    }

    private static IReadOnlyList<TicketCategoryResponse> BuildTree(List<TicketCategory> categories, Guid? parentId) =>
        [.. categories.Where(category => category.ParentId == parentId)
            .Select(category => Map(category, category.Fields, BuildTree(categories, category.Id)))];

    internal static TicketCategoryResponse Map(
        TicketCategory category,
        IEnumerable<TicketCustomField> fields,
        IReadOnlyList<TicketCategoryResponse>? children = null) => new(
        category.Id,
        category.Name,
        category.ParentId,
        category.IsActive,
        category.SortOrder,
        [.. fields.OrderBy(field => field.SortOrder).ThenBy(field => field.Label).Select(Map)],
        children ?? []);

    internal static CustomFieldResponse Map(TicketCustomField field) => new(
        field.Id,
        field.CategoryId,
        field.Key,
        field.Label,
        field.Type,
        field.IsRequired,
        field.Options,
        field.SortOrder);
}
