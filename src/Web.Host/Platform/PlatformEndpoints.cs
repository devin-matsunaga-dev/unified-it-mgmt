using System.Security.Claims;

using Platform.Auditing;

using Web.Host.Authentication;

namespace Web.Host.Platform;

public static class PlatformEndpoints
{
    public static IEndpointRouteBuilder MapPlatformEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/dev/audit-test", async (
            AuditTestRequest request,
            ClaimsPrincipal user,
            IAuditService auditService,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Value))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    [nameof(request.Value)] = ["Value is required."],
                });
            }

            await auditService.WriteAsync(user, "TestWritten", "PlatformTest", request.Id.ToString(), null, request, cancellationToken);
            return Results.NoContent();
        }).RequireAuthorization(AuthorizationPolicies.AdminOnly);

        return endpoints;
    }
}

public sealed record AuditTestRequest(Guid Id, string Value);