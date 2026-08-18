using System.Security.Claims;
using Microsoft.Extensions.Options;

namespace Web.Host.Authentication;

public static class AuthenticationEndpoints
{
    public static IEndpointRouteBuilder MapAuthenticationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/me", (ClaimsPrincipal user) =>
            Results.Ok(new CurrentUserResponse(
                user.FindFirstValue("sub") ?? user.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty,
                user.Identity?.Name ?? user.FindFirstValue("preferred_username") ?? string.Empty,
                // The sign-in name, which is the identity the helpdesk records against a ticket —
                // Keycloak mints its own subject id, so `sub` matches nothing the helpdesk stored.
                user.FindFirstValue("preferred_username") ?? user.Identity?.Name ?? string.Empty,
                user.FindFirstValue(ClaimTypes.Email) ?? user.FindFirstValue("email"),
                user.FindAll(ClaimTypes.Role).Select(claim => claim.Value).Distinct().Order().ToArray())))
            .RequireAuthorization();

        endpoints.MapGet("/api/admin/access-check", () => Results.NoContent())
            .RequireAuthorization(AuthorizationPolicies.AdminOnly);

        endpoints.MapGet("/api/auth/logout", (IOptions<AuthenticationOptions> configuredOptions) =>
        {
            var options = configuredOptions.Value;
            var logoutUri = $"{options.Authority.TrimEnd('/')}/protocol/openid-connect/logout" +
                $"?client_id={Uri.EscapeDataString(options.ClientId)}" +
                $"&post_logout_redirect_uri={Uri.EscapeDataString(options.PostLogoutRedirectUri)}";
            return Results.Redirect(logoutUri);
        });

        return endpoints;
    }
}

/// <param name="Username">
/// The sign-in name. Distinct from <paramref name="Id"/>, which is the OIDC subject: Keycloak mints
/// its own subject ids, so a ticket's assignee — recorded as the username — matches nothing else.
/// </param>
public sealed record CurrentUserResponse(
    string Id,
    string Name,
    string Username,
    string? Email,
    IReadOnlyList<string> Roles);
