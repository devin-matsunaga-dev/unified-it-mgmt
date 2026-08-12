using System.Security.Claims;

using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Platform.Data;

namespace Platform.Vault;

/// <summary>
/// The vault's HTTP surface.
/// <para>
/// Two audiences and two policies, the shape WP-3.2 gave the poller endpoints. Managing credentials is
/// administration and sits behind <c>AdminOnly</c> — not <c>CanManageMonitoring</c>, because a
/// technician who can edit a check should not thereby be able to replace the community string it
/// authenticates with. Redeeming a grant is the poller talking about itself and sits behind
/// <c>CanPoll</c>, which is disjoint from every operator policy: an administrator cannot redeem a
/// grant either, and there is deliberately no operator-facing path to material at all.
/// </para>
/// <para>
/// Note what is <em>not</em> here: no <c>GET</c> returns a secret, and there is no "reveal" endpoint.
/// The only route out of the vault is a redemption of a grant that Monitoring minted.
/// </para>
/// </summary>
public static class CredentialEndpoints
{
    public static IEndpointRouteBuilder MapCredentialEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        MapCredentials(endpoints);
        MapRedemptions(endpoints);
        return endpoints;
    }

    private static void MapCredentials(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/credentials").RequireAuthorization("AdminOnly");

        group.MapGet("/", async (Guid? siteId, CredentialKind? kind, ICredentialVault vault,
                CancellationToken cancellationToken) =>
            Results.Ok(await vault.ListAsync(siteId, kind, cancellationToken)));

        group.MapGet("/{id:guid}", async (Guid id, ICredentialVault vault, CancellationToken cancellationToken) =>
            await vault.GetAsync(id, cancellationToken) is { } credential
                ? Results.Ok(credential)
                : NotFound("Credential not found."));

        group.MapPost("/", async (CreateCredentialRequest request, ClaimsPrincipal user,
            ICredentialVault vault, CancellationToken cancellationToken) =>
        {
            var validation = await new CreateValidator().ValidateAsync(request, cancellationToken);
            if (!validation.IsValid) return Results.ValidationProblem(validation.ToDictionary());
            var result = await vault.CreateAsync(request, user, cancellationToken);
            return result.Outcome switch
            {
                CredentialOutcome.Success =>
                    Results.Created($"/api/credentials/{result.Credential!.Id}", result.Credential),
                CredentialOutcome.Invalid => Results.ValidationProblem(result.Errors!),
                CredentialOutcome.Conflict => Conflict(result.Error),
                var outcome => throw new InvalidOperationException($"Unknown credential outcome '{outcome}'."),
            };
        });

        group.MapPut("/{id:guid}", async (Guid id, UpdateCredentialRequest request, ClaimsPrincipal user,
            ICredentialVault vault, CancellationToken cancellationToken) =>
        {
            var validation = await new UpdateValidator().ValidateAsync(request, cancellationToken);
            if (!validation.IsValid) return Results.ValidationProblem(validation.ToDictionary());
            var result = await vault.UpdateAsync(id, request, user, cancellationToken);
            return result.Outcome switch
            {
                CredentialOutcome.Success => Results.Ok(result.Credential),
                CredentialOutcome.NotFound => NotFound("Credential not found."),
                CredentialOutcome.Invalid => Results.ValidationProblem(result.Errors!),
                CredentialOutcome.Conflict => Conflict(result.Error),
                var outcome => throw new InvalidOperationException($"Unknown credential outcome '{outcome}'."),
            };
        });

        // A rotation is a POST to a sub-resource rather than a PUT on the credential, following the
        // CONVENTIONS rule that a non-CRUD action is a POST: it is not an edit of the row, it is an
        // event in the credential's life, and the response says so by moving the version.
        group.MapPost("/{id:guid}/rotations", async (Guid id, RotateCredentialRequest request,
            ClaimsPrincipal user, ICredentialVault vault, CancellationToken cancellationToken) =>
        {
            var validation = await new RotateValidator().ValidateAsync(request, cancellationToken);
            if (!validation.IsValid) return Results.ValidationProblem(validation.ToDictionary());
            var result = await vault.RotateAsync(id, request, user, cancellationToken);
            return result.Outcome switch
            {
                CredentialOutcome.Success => Results.Ok(result.Credential),
                CredentialOutcome.NotFound => NotFound("Credential not found."),
                CredentialOutcome.Invalid => Results.ValidationProblem(result.Errors!),
                var outcome => throw new InvalidOperationException($"Unknown credential outcome '{outcome}'."),
            };
        });

        group.MapDelete("/{id:guid}", async (Guid id, ClaimsPrincipal user,
            ICredentialVault vault, CancellationToken cancellationToken) =>
        {
            var result = await vault.DeleteAsync(id, user, cancellationToken);
            return result.Outcome switch
            {
                CredentialOutcome.Success => Results.NoContent(),
                CredentialOutcome.NotFound => NotFound("Credential not found."),
                CredentialOutcome.Conflict => Conflict(result.Error),
                var outcome => throw new InvalidOperationException($"Unknown credential outcome '{outcome}'."),
            };
        });
    }

    private static void MapRedemptions(IEndpointRouteBuilder endpoints)
    {
        // The one door material leaves by. `CanPoll` is the Poller realm role and nothing else — not
        // Admin — so this endpoint cannot be reached with an operator's token at all.
        endpoints.MapPost("/api/credential-grants/redemptions", async (
                RedeemCredentialGrantRequest request, ClaimsPrincipal user,
                ICredentialVault vault, CancellationToken cancellationToken) =>
        {
            var result = await vault.RedeemGrantAsync(request, user, cancellationToken);
            return result.Outcome switch
            {
                CredentialOutcome.Success => Results.Ok(result.Released),
                // Deliberately the same answer for an unknown grant, a wrong token and a mismatched
                // pair: distinguishing them would turn this into an oracle.
                CredentialOutcome.NotFound => NotFound("No redeemable grant matches this token."),
                CredentialOutcome.Conflict => Conflict(result.Error),
                var outcome => throw new InvalidOperationException($"Unknown credential outcome '{outcome}'."),
            };
        }).RequireAuthorization("CanPoll");
    }

    private static IResult NotFound(string title) =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: title);

    private static IResult Conflict(string? detail) =>
        Results.Problem(statusCode: StatusCodes.Status409Conflict,
            title: "The request conflicts with the current state.", detail: detail);

    private sealed class CreateValidator : AbstractValidator<CreateCredentialRequest>
    {
        public CreateValidator()
        {
            RuleFor(request => request.Name).NotEmpty().MaximumLength(200);
            RuleFor(request => request.Description).MaximumLength(1_000);
            RuleFor(request => request.Kind).IsInEnum();
            // Only that there is one. What a secret must contain is `CredentialRules`, which knows the
            // kind — a per-property rule cannot see across the two.
            RuleFor(request => request.Material).NotEmpty();
        }
    }

    private sealed class UpdateValidator : AbstractValidator<UpdateCredentialRequest>
    {
        public UpdateValidator()
        {
            RuleFor(request => request.Name).NotEmpty().MaximumLength(200);
            RuleFor(request => request.Description).MaximumLength(1_000);
        }
    }

    private sealed class RotateValidator : AbstractValidator<RotateCredentialRequest>
    {
        public RotateValidator() => RuleFor(request => request.Material).NotEmpty();
    }
}
