using System.Security.Claims;
using System.Text.RegularExpressions;

using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Assets.Data;
using Modules.Assets.Features.Cis;

namespace Modules.Assets.Features.Contracts;

public static partial class ContractEndpoints
{
    public static IEndpointRouteBuilder MapContractEndpoints(this IEndpointRouteBuilder endpoints)
    {
        MapVendors(endpoints);
        MapContracts(endpoints);
        MapCoverage(endpoints);
        MapNotifications(endpoints);
        return endpoints;
    }

    private static void MapVendors(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/vendors").RequireAuthorization("CanManageAssets");

        group.MapGet("/", async (string? search, bool? isActive, int? page, int? pageSize,
                IVendorService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.ListAsync(
                new VendorListRequest(search, isActive, page ?? 1, pageSize ?? 25), cancellationToken)));

        group.MapGet("/{id:guid}", async (Guid id, IVendorService service, CancellationToken cancellationToken) =>
            await service.GetAsync(id, cancellationToken) is { } vendor
                ? Results.Ok(vendor)
                : Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Vendor not found."));

        group.MapPost("/", async (CreateVendorRequest request, ClaimsPrincipal user, IVendorService service,
            CancellationToken cancellationToken) =>
        {
            var validation = await new CreateVendorValidator().ValidateAsync(request, cancellationToken);
            if (!validation.IsValid) return Results.ValidationProblem(validation.ToDictionary());
            var result = await service.CreateAsync(request, user, cancellationToken);
            return result.Outcome switch
            {
                ContractOutcome.Success => Results.Created($"/api/vendors/{result.Vendor!.Id}", result.Vendor),
                ContractOutcome.Duplicate => Results.Problem(
                    statusCode: StatusCodes.Status409Conflict, title: "Vendor name is already used.",
                    detail: result.Error),
                var outcome => throw new InvalidOperationException($"Unknown contract outcome '{outcome}'."),
            };
        });

        group.MapPut("/{id:guid}", async (Guid id, UpdateVendorRequest request, ClaimsPrincipal user,
            IVendorService service, CancellationToken cancellationToken) =>
        {
            var validation = await new UpdateVendorValidator().ValidateAsync(request, cancellationToken);
            if (!validation.IsValid) return Results.ValidationProblem(validation.ToDictionary());
            var result = await service.UpdateAsync(id, request, user, cancellationToken);
            return result.Outcome switch
            {
                ContractOutcome.Success => Results.Ok(result.Vendor),
                ContractOutcome.NotFound => Results.Problem(
                    statusCode: StatusCodes.Status404NotFound, title: "Vendor not found."),
                ContractOutcome.Duplicate => Results.Problem(
                    statusCode: StatusCodes.Status409Conflict, title: "Vendor name is already used.",
                    detail: result.Error),
                var outcome => throw new InvalidOperationException($"Unknown contract outcome '{outcome}'."),
            };
        });

        group.MapDelete("/{id:guid}", async (Guid id, ClaimsPrincipal user, IVendorService service,
            CancellationToken cancellationToken) => await service.DeleteAsync(id, user, cancellationToken) switch
        {
            ContractOutcome.Success => Results.NoContent(),
            ContractOutcome.NotFound => Results.Problem(
                statusCode: StatusCodes.Status404NotFound, title: "Vendor not found."),
            ContractOutcome.InUse => Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Vendor is in use.",
                detail: "Delete or reassign the vendor's contracts before deleting it."),
            var outcome => throw new InvalidOperationException($"Unknown contract outcome '{outcome}'."),
        });
    }

    private static void MapContracts(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/contracts").RequireAuthorization("CanManageAssets");

        // Read by anyone who may see contracts, written by an administrator: the thresholds decide
        // who gets told what and when, which is a policy rather than a preference.
        var reminders = endpoints.MapGroup("/api/contract-reminder-settings")
            .RequireAuthorization("CanManageAssets");

        reminders.MapGet("/", async (IContractReminderSettingsService service,
                CancellationToken cancellationToken) =>
            Results.Ok(await service.GetAsync(cancellationToken)));

        reminders.MapPut("/", async (SaveContractReminderSettingsRequest request, ClaimsPrincipal user,
            IContractReminderSettingsService service, CancellationToken cancellationToken) =>
        {
            var validation = await new ReminderSettingsValidator().ValidateAsync(request, cancellationToken);
            return validation.IsValid
                ? Results.Ok(await service.SaveAsync(request, user, cancellationToken))
                : Results.ValidationProblem(validation.ToDictionary());
        }).RequireAuthorization("AdminOnly");

        group.MapGet("/", async (string? search, Guid? vendorId, Guid? departmentId,
                ContractExpiryStatus? status, ContractType? type,
                bool? isActive, int? page, int? pageSize, IContractService service,
                CancellationToken cancellationToken) =>
            Results.Ok(await service.ListAsync(
                new ContractListRequest(search, vendorId, departmentId, status, type, isActive,
                    page ?? 1, pageSize ?? 25),
                cancellationToken)));

        group.MapGet("/{id:guid}", async (Guid id, IContractService service, CancellationToken cancellationToken) =>
            await service.GetAsync(id, cancellationToken) is { } contract
                ? Results.Ok(contract)
                : Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Contract not found."));

        group.MapPost("/", async (CreateContractRequest request, ClaimsPrincipal user, IContractService service,
            CancellationToken cancellationToken) =>
        {
            var validation = await new CreateContractValidator().ValidateAsync(request, cancellationToken);
            if (!validation.IsValid) return Results.ValidationProblem(validation.ToDictionary());
            var result = await service.CreateAsync(request, user, cancellationToken);
            return result.Outcome switch
            {
                ContractOutcome.Success => Results.Created($"/api/contracts/{result.Contract!.Id}", result.Contract),
                ContractOutcome.Invalid => Results.ValidationProblem(result.Errors!),
                ContractOutcome.Duplicate => Results.Problem(
                    statusCode: StatusCodes.Status409Conflict, title: "Contract number is already used.",
                    detail: result.Error),
                var outcome => throw new InvalidOperationException($"Unknown contract outcome '{outcome}'."),
            };
        });

        group.MapPut("/{id:guid}", async (Guid id, UpdateContractRequest request, ClaimsPrincipal user,
            IContractService service, CancellationToken cancellationToken) =>
        {
            var validation = await new UpdateContractValidator().ValidateAsync(request, cancellationToken);
            if (!validation.IsValid) return Results.ValidationProblem(validation.ToDictionary());
            var result = await service.UpdateAsync(id, request, user, cancellationToken);
            return result.Outcome switch
            {
                ContractOutcome.Success => Results.Ok(result.Contract),
                ContractOutcome.NotFound => Results.Problem(
                    statusCode: StatusCodes.Status404NotFound, title: "Contract not found."),
                ContractOutcome.Invalid => Results.ValidationProblem(result.Errors!),
                ContractOutcome.Duplicate => Results.Problem(
                    statusCode: StatusCodes.Status409Conflict, title: "Contract number is already used.",
                    detail: result.Error),
                var outcome => throw new InvalidOperationException($"Unknown contract outcome '{outcome}'."),
            };
        });

        group.MapDelete("/{id:guid}", async (Guid id, ClaimsPrincipal user, IContractService service,
            CancellationToken cancellationToken) => await service.DeleteAsync(id, user, cancellationToken) switch
        {
            ContractOutcome.Success => Results.NoContent(),
            ContractOutcome.NotFound => Results.Problem(
                statusCode: StatusCodes.Status404NotFound, title: "Contract not found."),
            ContractOutcome.InUse => Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Contract is in use.",
                detail: "Release the CIs this contract covers before deleting it."),
            var outcome => throw new InvalidOperationException($"Unknown contract outcome '{outcome}'."),
        });
    }

    private static void MapCoverage(IEndpointRouteBuilder endpoints)
    {
        // Coverage is a complete statement, following the WP-2.2 assignment endpoint: an omitted
        // contract releases the CI rather than leaving the previous one in place.
        endpoints.MapPut("/api/cis/{id:guid}/coverage", async (Guid id, SetCiCoverageRequest request,
                ClaimsPrincipal user, IContractService service, CancellationToken cancellationToken) =>
            {
                var validation = await new SetCiCoverageValidator().ValidateAsync(request, cancellationToken);
                if (!validation.IsValid) return Results.ValidationProblem(validation.ToDictionary());
                var result = await service.SetCoverageAsync(id, request, user, cancellationToken);
                return result.Outcome switch
                {
                    CiOutcome.Success => Results.Ok(result.Ci),
                    CiOutcome.NotFound => Results.Problem(
                        statusCode: StatusCodes.Status404NotFound, title: "CI not found."),
                    CiOutcome.InvalidAttributes => Results.ValidationProblem(result.Errors!),
                    CiOutcome.Disposed => Results.Problem(
                        statusCode: StatusCodes.Status409Conflict, title: "CI is disposed.",
                        detail: result.Error),
                    var outcome => throw new InvalidOperationException($"Unknown CI outcome '{outcome}'."),
                };
            })
            .RequireAuthorization("CanManageAssets");
    }

    private static void MapNotifications(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/contract-notifications").RequireAuthorization("CanManageAssets");

        group.MapGet("/", async (int? limit, IContractExpiryService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.ListNotificationsAsync(limit ?? 50, cancellationToken)));

        // The scheduled pass runs daily, but the dev database is recreated on most AppHost restarts,
        // so a fixture created by hand would never survive until the next scheduled run. The trigger
        // is safe to expose because the pass is idempotent.
        group.MapPost("/runs", async (IContractExpiryService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.RunAsync(cancellationToken)));
    }

    private sealed class CreateVendorValidator : AbstractValidator<CreateVendorRequest>
    {
        public CreateVendorValidator()
        {
            RuleFor(request => request.Name).NotEmpty().MaximumLength(200);
            RuleFor(request => request.ContactName).MaximumLength(200);
            RuleFor(request => request.ContactEmail).MaximumLength(320).EmailAddress()
                .When(request => !string.IsNullOrWhiteSpace(request.ContactEmail));
            RuleFor(request => request.ContactPhone).MaximumLength(50);
            RuleFor(request => request.Website).MaximumLength(500);
            RuleFor(request => request.Notes).MaximumLength(2_000);
        }
    }

    private sealed class UpdateVendorValidator : AbstractValidator<UpdateVendorRequest>
    {
        public UpdateVendorValidator()
        {
            RuleFor(request => request.Name).NotEmpty().MaximumLength(200);
            RuleFor(request => request.ContactName).MaximumLength(200);
            RuleFor(request => request.ContactEmail).MaximumLength(320).EmailAddress()
                .When(request => !string.IsNullOrWhiteSpace(request.ContactEmail));
            RuleFor(request => request.ContactPhone).MaximumLength(50);
            RuleFor(request => request.Website).MaximumLength(500);
            RuleFor(request => request.Notes).MaximumLength(2_000);
        }
    }

    private sealed class CreateContractValidator : AbstractValidator<CreateContractRequest>
    {
        public CreateContractValidator()
        {
            RuleFor(request => request.VendorId).NotEqual(Guid.Empty);
            RuleFor(request => request.PoNumber).NotEmpty().MaximumLength(100);
            RuleFor(request => request.Name).NotEmpty().MaximumLength(200);
            RuleFor(request => request.Type).IsInEnum();
            RuleFor(request => request.EndDate).GreaterThanOrEqualTo(request => request.StartDate)
                .WithMessage("The end date cannot be before the start date.");
            RuleFor(request => request.Cost).GreaterThanOrEqualTo(0).When(request => request.Cost is not null);
            RuleFor(request => request.Currency).Length(3).When(request => !string.IsNullOrWhiteSpace(request.Currency));
            RuleFor(request => request.Notes).MaximumLength(2_000);
        }
    }

    private sealed class UpdateContractValidator : AbstractValidator<UpdateContractRequest>
    {
        public UpdateContractValidator()
        {
            RuleFor(request => request.VendorId).NotEqual(Guid.Empty);
            RuleFor(request => request.PoNumber).NotEmpty().MaximumLength(100);
            RuleFor(request => request.Name).NotEmpty().MaximumLength(200);
            RuleFor(request => request.Type).IsInEnum();
            RuleFor(request => request.EndDate).GreaterThanOrEqualTo(request => request.StartDate)
                .WithMessage("The end date cannot be before the start date.");
            RuleFor(request => request.Cost).GreaterThanOrEqualTo(0).When(request => request.Cost is not null);
            RuleFor(request => request.Currency).Length(3).When(request => !string.IsNullOrWhiteSpace(request.Currency));
            RuleFor(request => request.Notes).MaximumLength(2_000);
        }
    }

    private sealed class SetCiCoverageValidator : AbstractValidator<SetCiCoverageRequest>
    {
        public SetCiCoverageValidator()
        {
            RuleFor(request => request.ContractId).NotEqual(Guid.Empty);
            RuleFor(request => request.WarrantyExpiresAt)
                .GreaterThanOrEqualTo(request => request.PurchaseDate!.Value)
                .When(request => request.PurchaseDate is not null && request.WarrantyExpiresAt is not null)
                .WithMessage("A warranty cannot end before the asset was bought.");
        }
    }

    private sealed partial class ReminderSettingsValidator : AbstractValidator<SaveContractReminderSettingsRequest>
    {
        /// <summary>
        /// Deliberately loose: one @, something either side, a dot in the domain. Tighter patterns
        /// reject addresses that are legal and in use, and the only authority on deliverability is the
        /// mail server.
        /// </summary>
        [GeneratedRegex(@"^[^@\s]+@[^@\s.]+(\.[^@\s.]+)+$")]
        private static partial Regex EmailShape();

        public ReminderSettingsValidator()
        {
            RuleFor(request => request.ThresholdDays).NotNull();
            RuleFor(request => request.ThresholdDays.Count)
                .InclusiveBetween(1, ContractReminderSettingsService.MaximumThresholds)
                .WithMessage($"Between 1 and {ContractReminderSettingsService.MaximumThresholds} reminders.");
            // Zero is the expiry day itself and is a legitimate choice; negative is a notice after the
            // fact, which is a different feature and not this one.
            RuleFor(request => request.Recipients!.Count)
                .LessThanOrEqualTo(ContractReminderSettingsService.MaximumRecipients)
                .When(request => request.Recipients is not null)
                .WithMessage($"At most {ContractReminderSettingsService.MaximumRecipients} addresses — use a distribution group for a wider audience.");

            // Shape only. Whether the mailbox exists is the mail server's answer, not something this
            // screen can know, and a rejected send is already recorded against the notice.
            RuleForEach(request => request.Recipients)
                .Must(address => !string.IsNullOrWhiteSpace(address)
                    && address.Trim().Length <= ContractReminderSettingsService.MaximumRecipientLength
                    && EmailShape().IsMatch(address.Trim()))
                .WithMessage("Each entry must be an email address.");

            RuleForEach(request => request.ThresholdDays)
                .InclusiveBetween(0, ContractReminderSettingsService.MaximumThresholdDays)
                .WithMessage($"A reminder is 0 to {ContractReminderSettingsService.MaximumThresholdDays} days before expiry.");
        }
    }
}
