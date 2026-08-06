using System.Security.Claims;
using Web.Host.Authentication;

namespace Infrastructure.Tests;

public sealed class KeycloakRoleClaimsTransformationTests
{
    [Fact]
    public async Task TransformAsync_RealmRoles_AddsRoleClaimsWithoutDuplicates()
    {
        var identity = new ClaimsIdentity(
            [new Claim("realm_access", "{\"roles\":[\"Admin\",\"Technician\",\"offline_access\"]}")],
            "Bearer");
        var principal = new ClaimsPrincipal(identity);
        var transformation = new KeycloakRoleClaimsTransformation();

        await transformation.TransformAsync(principal);
        await transformation.TransformAsync(principal);

        Assert.True(principal.IsInRole("Admin"));
        Assert.True(principal.IsInRole("Technician"));
        Assert.False(principal.IsInRole("offline_access"));
        Assert.Equal(2, principal.FindAll(ClaimTypes.Role).Count());
    }

    [Fact]
    public async Task TransformAsync_MissingRealmRoles_DoesNotGrantRole()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([], "Bearer"));
        var transformation = new KeycloakRoleClaimsTransformation();

        await transformation.TransformAsync(principal);

        Assert.Empty(principal.FindAll(ClaimTypes.Role));
    }
}
