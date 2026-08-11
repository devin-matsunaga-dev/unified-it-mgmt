using System.Security.Claims;

using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Platform.Data;

namespace Platform.Notifications;

/// <summary>
/// The notification configuration surface. Channels and routing rules are administration — a rule
/// decides who is woken at three in the morning — so they sit behind <c>AdminOnly</c>; a person's own
/// preference is theirs and needs nothing but a sign-in.
/// </summary>
public static class NotificationEndpoints
{
    public static IEndpointRouteBuilder MapNotificationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        MapChannels(endpoints);
        MapRules(endpoints);
        MapPreferences(endpoints);
        MapDeliveries(endpoints);
        return endpoints;
    }

    private static void MapChannels(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/notification-channels").RequireAuthorization("AdminOnly");

        group.MapGet("/", async (INotificationRoutingService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.ListChannelsAsync(cancellationToken)));

        group.MapPost("/", async (CreateNotificationChannelRequest request, ClaimsPrincipal user,
            INotificationRoutingService service, CancellationToken cancellationToken) =>
        {
            var validation = await new CreateChannelValidator().ValidateAsync(request, cancellationToken);
            if (!validation.IsValid) return Results.ValidationProblem(validation.ToDictionary());
            var result = await service.CreateChannelAsync(request, user, cancellationToken);
            return result.Outcome switch
            {
                NotificationOutcome.Success =>
                    Results.Created($"/api/notification-channels/{result.Channel!.Id}", result.Channel),
                NotificationOutcome.Invalid => Results.ValidationProblem(result.Errors!),
                NotificationOutcome.Conflict => Conflict(result.Error),
                var outcome => throw new InvalidOperationException($"Unknown notification outcome '{outcome}'."),
            };
        });

        group.MapPut("/{id:guid}", async (Guid id, UpdateNotificationChannelRequest request, ClaimsPrincipal user,
            INotificationRoutingService service, CancellationToken cancellationToken) =>
        {
            var validation = await new UpdateChannelValidator().ValidateAsync(request, cancellationToken);
            if (!validation.IsValid) return Results.ValidationProblem(validation.ToDictionary());
            var result = await service.UpdateChannelAsync(id, request, user, cancellationToken);
            return result.Outcome switch
            {
                NotificationOutcome.Success => Results.Ok(result.Channel),
                NotificationOutcome.NotFound => NotFound("Notification channel not found."),
                NotificationOutcome.Invalid => Results.ValidationProblem(result.Errors!),
                NotificationOutcome.Conflict => Conflict(result.Error),
                var outcome => throw new InvalidOperationException($"Unknown notification outcome '{outcome}'."),
            };
        });

        group.MapDelete("/{id:guid}", async (Guid id, ClaimsPrincipal user,
            INotificationRoutingService service, CancellationToken cancellationToken) =>
        {
            var result = await service.DeleteChannelAsync(id, user, cancellationToken);
            return result.Outcome switch
            {
                NotificationOutcome.Success => Results.NoContent(),
                NotificationOutcome.NotFound => NotFound("Notification channel not found."),
                NotificationOutcome.Conflict => Conflict(result.Error),
                var outcome => throw new InvalidOperationException($"Unknown notification outcome '{outcome}'."),
            };
        });
    }

    private static void MapRules(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/notification-routing-rules").RequireAuthorization("AdminOnly");

        group.MapGet("/", async (INotificationRoutingService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.ListRulesAsync(cancellationToken)));

        group.MapPost("/", async (SaveNotificationRoutingRuleRequest request, ClaimsPrincipal user,
            INotificationRoutingService service, CancellationToken cancellationToken) =>
        {
            var validation = await new SaveRuleValidator().ValidateAsync(request, cancellationToken);
            if (!validation.IsValid) return Results.ValidationProblem(validation.ToDictionary());
            var result = await service.CreateRuleAsync(request, user, cancellationToken);
            return result.Outcome switch
            {
                NotificationOutcome.Success =>
                    Results.Created($"/api/notification-routing-rules/{result.Rule!.Id}", result.Rule),
                NotificationOutcome.Invalid => Results.ValidationProblem(result.Errors!),
                NotificationOutcome.Conflict => Conflict(result.Error),
                var outcome => throw new InvalidOperationException($"Unknown notification outcome '{outcome}'."),
            };
        });

        group.MapPut("/{id:guid}", async (Guid id, SaveNotificationRoutingRuleRequest request, ClaimsPrincipal user,
            INotificationRoutingService service, CancellationToken cancellationToken) =>
        {
            var validation = await new SaveRuleValidator().ValidateAsync(request, cancellationToken);
            if (!validation.IsValid) return Results.ValidationProblem(validation.ToDictionary());
            var result = await service.UpdateRuleAsync(id, request, user, cancellationToken);
            return result.Outcome switch
            {
                NotificationOutcome.Success => Results.Ok(result.Rule),
                NotificationOutcome.NotFound => NotFound("Routing rule not found."),
                NotificationOutcome.Invalid => Results.ValidationProblem(result.Errors!),
                NotificationOutcome.Conflict => Conflict(result.Error),
                var outcome => throw new InvalidOperationException($"Unknown notification outcome '{outcome}'."),
            };
        });

        group.MapDelete("/{id:guid}", async (Guid id, ClaimsPrincipal user,
                INotificationRoutingService service, CancellationToken cancellationToken) =>
            await service.DeleteRuleAsync(id, user, cancellationToken) switch
            {
                NotificationOutcome.Success => Results.NoContent(),
                NotificationOutcome.NotFound => NotFound("Routing rule not found."),
                var outcome => throw new InvalidOperationException($"Unknown notification outcome '{outcome}'."),
            });
    }

    private static void MapPreferences(IEndpointRouteBuilder endpoints)
    {
        // Not AdminOnly and not an agent policy: everybody who can sign in has notifications, and an
        // EndUser muting their own ticket mail is the point of the feature.
        var group = endpoints.MapGroup("/api/notification-preferences").RequireAuthorization();

        group.MapGet("/me", async (ClaimsPrincipal user, INotificationRoutingService service,
                CancellationToken cancellationToken) =>
            Results.Ok(await service.GetPreferenceAsync(ActorId(user), cancellationToken)));

        group.MapPut("/me", async (SaveUserNotificationPreferenceRequest request, ClaimsPrincipal user,
            INotificationRoutingService service, CancellationToken cancellationToken) =>
        {
            var validation = await new SavePreferenceValidator().ValidateAsync(request, cancellationToken);
            if (!validation.IsValid) return Results.ValidationProblem(validation.ToDictionary());
            var result = await service.SavePreferenceAsync(request, user, cancellationToken);
            return result.Outcome switch
            {
                NotificationOutcome.Success => Results.Ok(result.Preference),
                NotificationOutcome.Invalid => Results.ValidationProblem(result.Errors!),
                var outcome => throw new InvalidOperationException($"Unknown notification outcome '{outcome}'."),
            };
        });
    }

    private static void MapDeliveries(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/notification-deliveries").RequireAuthorization("AdminOnly");

        group.MapGet("/", async (string? eventKind, NotificationDeliveryOutcome? outcome, Guid? channelId,
                string? userId, int? page, int? pageSize, INotificationRoutingService service,
                CancellationToken cancellationToken) =>
            Results.Ok(await service.ListDeliveriesAsync(
                new NotificationDeliveryListRequest(eventKind, outcome, channelId, userId, page ?? 1, pageSize ?? 25),
                cancellationToken)));

        // The same reason WP-2.6 put a manual trigger beside the contract-expiry job: the dev database
        // is recreated on most AppHost restarts, so a quiet-hours fixture made by hand would never
        // survive to the next scheduled pass. The pass is safe to repeat.
        endpoints.MapPost("/api/notification-digests/runs", async (
                INotificationDigestService digestService, CancellationToken cancellationToken) =>
            Results.Ok(await digestService.RunAsync(DateTimeOffset.UtcNow, cancellationToken)))
            .RequireAuthorization("AdminOnly");
    }

    private static string ActorId(ClaimsPrincipal actor) =>
        actor.FindFirstValue("sub") ?? actor.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException("An authenticated actor identifier is required.");

    private static IResult NotFound(string title) =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: title);

    private static IResult Conflict(string? detail) =>
        Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "The request conflicts with the current state.",
            detail: detail);

    private sealed class CreateChannelValidator : AbstractValidator<CreateNotificationChannelRequest>
    {
        public CreateChannelValidator()
        {
            RuleFor(request => request.Name).NotEmpty().MaximumLength(200);
            RuleFor(request => request.Target).NotEmpty().MaximumLength(2_000);
            RuleFor(request => request.Description).MaximumLength(1_000);
            RuleFor(request => request.Kind).IsInEnum();
        }
    }

    private sealed class UpdateChannelValidator : AbstractValidator<UpdateNotificationChannelRequest>
    {
        public UpdateChannelValidator()
        {
            RuleFor(request => request.Name).NotEmpty().MaximumLength(200);
            RuleFor(request => request.Target).MaximumLength(2_000);
            RuleFor(request => request.Description).MaximumLength(1_000);
        }
    }

    private sealed class SaveRuleValidator : AbstractValidator<SaveNotificationRoutingRuleRequest>
    {
        public SaveRuleValidator()
        {
            RuleFor(request => request.Name).NotEmpty().MaximumLength(200);
            RuleFor(request => request.ChannelId).NotEmpty();
            RuleFor(request => request.EventKind).MaximumLength(100);
            RuleFor(request => request.DeviceGroup).MaximumLength(100);
            RuleFor(request => request.TimeZone).MaximumLength(100);
            RuleFor(request => request.MinimumSeverity).IsInEnum();
        }
    }

    private sealed class SavePreferenceValidator : AbstractValidator<SaveUserNotificationPreferenceRequest>
    {
        public SavePreferenceValidator()
        {
            RuleFor(request => request.EmailAddress).MaximumLength(320);
            RuleFor(request => request.TimeZone).MaximumLength(100);
            RuleFor(request => request.MinimumSeverity).IsInEnum();
        }
    }
}
