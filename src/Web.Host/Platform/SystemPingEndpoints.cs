using System.Security.Claims;

using Platform.Messaging;

using Web.Host.Authentication;

namespace Web.Host.Platform;

public static class SystemPingEndpoints
{
    public static IEndpointRouteBuilder MapSystemPingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/dev/ping", PublishAsync)
            .RequireAuthorization(AuthorizationPolicies.AdminOnly);

        return endpoints;
    }

    private static async Task<IResult> PublishAsync(
        PublishSystemPingRequest request,
        ClaimsPrincipal actor,
        ISystemPingPublisher publisher,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.DedupeKey) || request.DedupeKey.Length > 200)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                [nameof(request.DedupeKey)] = ["Dedupe key is required and must not exceed 200 characters."],
            });
        }

        var ping = await publisher.PublishAsync(request.DedupeKey.Trim(), actor, cancellationToken);

        return Results.Accepted(value: new PublishSystemPingResponse(ping.EventId, ping.DedupeKey));
    }
}

public sealed record PublishSystemPingRequest(string DedupeKey);

public sealed record PublishSystemPingResponse(Guid EventId, string DedupeKey);
