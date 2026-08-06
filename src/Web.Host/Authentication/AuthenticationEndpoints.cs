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

public sealed record CurrentUserResponse(string Id, string Name, string? Email, IReadOnlyList<string> Roles);
