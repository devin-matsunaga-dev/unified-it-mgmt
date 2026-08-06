using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;

namespace Web.Host.Authentication;

public sealed class KeycloakRoleClaimsTransformation : IClaimsTransformation
{
    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity is not ClaimsIdentity identity || !identity.IsAuthenticated)
        {
            return Task.FromResult(principal);
        }

        var realmAccess = principal.FindFirst("realm_access")?.Value;
        if (string.IsNullOrWhiteSpace(realmAccess))
        {
            return Task.FromResult(principal);
        }

        using var document = JsonDocument.Parse(realmAccess);
        if (!document.RootElement.TryGetProperty("roles", out var roles) ||
            roles.ValueKind is not JsonValueKind.Array)
        {
            return Task.FromResult(principal);
        }

        foreach (var role in roles.EnumerateArray())
        {
            var roleName = role.GetString();
            if (roleName is not null && PlatformRoles.All.Contains(roleName) && !principal.IsInRole(roleName))
            {
                identity.AddClaim(new Claim(identity.RoleClaimType, roleName));
            }
        }

        return Task.FromResult(principal);
    }
}
