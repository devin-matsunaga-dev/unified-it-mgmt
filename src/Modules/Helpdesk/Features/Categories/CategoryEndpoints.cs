using System.Security.Claims;

using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Helpdesk.Data;

namespace Modules.Helpdesk.Features.Categories;

public static class CategoryEndpoints
{
    public static IEndpointRouteBuilder MapCategoryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/ticket-categories", async (bool? includeInactive, ICategoryService service,
                CancellationToken cancellationToken) =>
                Results.Ok(await service.GetTreeAsync(includeInactive ?? false, cancellationToken)))
            .RequireAuthorization("CanManageTickets");

        var admin = endpoints.MapGroup("/api/ticket-categories").RequireAuthorization("AdminOnly");

        admin.MapPost("/", async (CreateTicketCategoryRequest request, ClaimsPrincipal user,
            ICategoryService service, CancellationToken cancellationToken) =>
        {
            var validation = await new CreateCategoryValidator().ValidateAsync(request, cancellationToken);
            if (!validation.IsValid) return Results.ValidationProblem(validation.ToDictionary());
            var result = await service.CreateAsync(request, user, cancellationToken);
            return result.Outcome switch
            {
                CategoryOutcome.Success => Results.Created($"/api/ticket-categories/{result.Category!.Id}", result.Category),
                CategoryOutcome.ParentNotFound => Results.ValidationProblem(
                    new Dictionary<string, string[]> { [nameof(request.ParentId)] = [result.Error!] }),
                CategoryOutcome.CycleDetected => Results.ValidationProblem(
                    new Dictionary<string, string[]> { [nameof(request.ParentId)] = [result.Error!] }),
                CategoryOutcome.DuplicateName => Results.Problem(
                    statusCode: StatusCodes.Status409Conflict, title: "Category name is already used.", detail: result.Error),
                _ => throw new InvalidOperationException($"Unknown category outcome '{result.Outcome}'."),
            };
        });

        admin.MapPut("/{id:guid}", async (Guid id, UpdateTicketCategoryRequest request, ClaimsPrincipal user,
            ICategoryService service, CancellationToken cancellationToken) =>
        {
            var validation = await new UpdateCategoryValidator().ValidateAsync(request, cancellationToken);
            if (!validation.IsValid) return Results.ValidationProblem(validation.ToDictionary());
            var result = await service.UpdateAsync(id, request, user, cancellationToken);
            return result.Outcome switch
            {
                CategoryOutcome.Success => Results.Ok(result.Category),
                CategoryOutcome.NotFound => Results.Problem(
                    statusCode: StatusCodes.Status404NotFound, title: "Category not found."),
                CategoryOutcome.ParentNotFound or CategoryOutcome.CycleDetected => Results.ValidationProblem(
                    new Dictionary<string, string[]> { [nameof(request.ParentId)] = [result.Error!] }),
                CategoryOutcome.DuplicateName => Results.Problem(
                    statusCode: StatusCodes.Status409Conflict, title: "Category name is already used.", detail: result.Error),
                _ => throw new InvalidOperationException($"Unknown category outcome '{result.Outcome}'."),
            };
        });

        admin.MapDelete("/{id:guid}", async (Guid id, ClaimsPrincipal user, ICategoryService service,
            CancellationToken cancellationToken) => await service.DeleteAsync(id, user, cancellationToken) switch
        {
            CategoryOutcome.Success => Results.NoContent(),
            CategoryOutcome.NotFound => Results.Problem(
                statusCode: StatusCodes.Status404NotFound, title: "Category not found."),
            CategoryOutcome.InUse => Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Category is in use.",
                detail: "Categories with child categories or tickets cannot be deleted; deactivate it instead."),
            var outcome => throw new InvalidOperationException($"Unknown category outcome '{outcome}'."),
        });

        admin.MapPost("/{categoryId:guid}/fields", async (Guid categoryId, CreateCustomFieldRequest request,
            ClaimsPrincipal user, ICategoryService service, CancellationToken cancellationToken) =>
        {
            var validation = await new CreateCustomFieldValidator().ValidateAsync(request, cancellationToken);
            if (!validation.IsValid) return Results.ValidationProblem(validation.ToDictionary());
            var result = await service.AddFieldAsync(categoryId, request, user, cancellationToken);
            return result.Outcome switch
            {
                CategoryOutcome.Success => Results.Created(
                    $"/api/ticket-categories/{categoryId}/fields/{result.Field!.Id}", result.Field),
                CategoryOutcome.NotFound => Results.Problem(
                    statusCode: StatusCodes.Status404NotFound, title: "Category not found."),
                CategoryOutcome.DuplicateName => Results.Problem(
                    statusCode: StatusCodes.Status409Conflict, title: "Field key is already used.", detail: result.Error),
                _ => throw new InvalidOperationException($"Unknown category outcome '{result.Outcome}'."),
            };
        });

        admin.MapDelete("/{categoryId:guid}/fields/{fieldId:guid}", async (Guid categoryId, Guid fieldId,
            ClaimsPrincipal user, ICategoryService service, CancellationToken cancellationToken) =>
            await service.DeleteFieldAsync(categoryId, fieldId, user, cancellationToken) switch
            {
                CategoryOutcome.Success => Results.NoContent(),
                CategoryOutcome.NotFound => Results.Problem(
                    statusCode: StatusCodes.Status404NotFound, title: "Custom field not found."),
                CategoryOutcome.InUse => Results.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    title: "Custom field is in use.",
                    detail: "Fields that already hold ticket values cannot be deleted."),
                var outcome => throw new InvalidOperationException($"Unknown category outcome '{outcome}'."),
            });

        return endpoints;
    }

    private sealed class CreateCategoryValidator : AbstractValidator<CreateTicketCategoryRequest>
    {
        public CreateCategoryValidator()
        {
            RuleFor(request => request.Name).NotEmpty().MaximumLength(100);
            RuleFor(request => request.SortOrder).InclusiveBetween(0, 10_000);
        }
    }

    private sealed class UpdateCategoryValidator : AbstractValidator<UpdateTicketCategoryRequest>
    {
        public UpdateCategoryValidator()
        {
            RuleFor(request => request.Name).NotEmpty().MaximumLength(100);
            RuleFor(request => request.SortOrder).InclusiveBetween(0, 10_000);
        }
    }

    private sealed class CreateCustomFieldValidator : AbstractValidator<CreateCustomFieldRequest>
    {
        public CreateCustomFieldValidator()
        {
            RuleFor(request => request.Key).NotEmpty().MaximumLength(50)
                .Matches("^[a-zA-Z][a-zA-Z0-9_]*$")
                .WithMessage("Key must start with a letter and contain only letters, digits, and underscores.");
            RuleFor(request => request.Label).NotEmpty().MaximumLength(100);
            RuleFor(request => request.Type).IsInEnum();
            RuleFor(request => request.SortOrder).InclusiveBetween(0, 10_000);
            RuleFor(request => request.Options)
                .Must(options => options is { Count: > 0 } && options.All(option => !string.IsNullOrWhiteSpace(option)))
                .When(request => request.Type == CustomFieldType.Select)
                .WithMessage("Select fields require at least one non-empty option.");
            RuleForEach(request => request.Options).MaximumLength(100)
                .When(request => request.Options is not null);
        }
    }
}
