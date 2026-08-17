using System.Security.Claims;

using Platform.Dashboards;

namespace Web.Host.Platform;

/// <summary>
/// The unified dashboard (WP-5.5): one read for the whole screen, and the writes that keep several named
/// views of it.
/// <para>
/// Behind plain authentication rather than an operator policy, following WP-5.4's search endpoint. Every
/// widget the platform currently has is agent-only and says so itself, so an end user reaching this gets a
/// layout with nothing in it — which is the honest answer and is what <c>HomeRoute</c> already prevents by
/// sending them to the portal. Putting a policy here instead would decide once, at the door, a question
/// that belongs to each widget: WP-5.9's knowledge base may well want a widget an end user can see.
/// </para>
/// </summary>
public static class UnifiedDashboardEndpoints
{
    public static IEndpointRouteBuilder MapUnifiedDashboardEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var dashboard = endpoints.MapGroup("/api/dashboard").RequireAuthorization();

        dashboard.MapGet("", async (
                IDashboardService service,
                ClaimsPrincipal actor,
                CancellationToken cancellationToken) =>
            Results.Ok(await service.GetAsync(actor, cancellationToken)));

        dashboard.MapPost("/views", async (
            SaveDashboardViewPayload? payload,
            IDashboardService service,
            ClaimsPrincipal actor,
            CancellationToken cancellationToken) =>
        {
            // A name is required on create and optional on update: a view has to be called something to be
            // a tab, but saving an arrangement should not have to send the name back to keep it.
            if (Validate(payload, nameRequired: true) is { Count: > 0 } errors)
            {
                return Results.ValidationProblem(errors);
            }

            var result = await service.CreateViewAsync(Request(payload!), actor, cancellationToken);
            return Answer(result, created: true);
        });

        dashboard.MapPut("/views/{viewId:guid}", async (
            Guid viewId,
            SaveDashboardViewPayload? payload,
            IDashboardService service,
            ClaimsPrincipal actor,
            CancellationToken cancellationToken) =>
        {
            if (Validate(payload, nameRequired: false) is { Count: > 0 } errors)
            {
                return Results.ValidationProblem(errors);
            }

            return Answer(await service.SaveViewAsync(viewId, Request(payload!), actor, cancellationToken));
        });

        dashboard.MapPost("/views/{viewId:guid}/selection", async (
                Guid viewId,
                IDashboardService service,
                ClaimsPrincipal actor,
                CancellationToken cancellationToken) =>
            Answer(await service.SelectViewAsync(viewId, actor, cancellationToken)));

        dashboard.MapDelete("/views/{viewId:guid}", async (
                Guid viewId,
                IDashboardService service,
                ClaimsPrincipal actor,
                CancellationToken cancellationToken) =>
            Answer(await service.DeleteViewAsync(viewId, actor, cancellationToken)));

        return endpoints;
    }

    /// <summary>
    /// One outcome vocabulary, mapped once. CONVENTIONS: a validation failure is a 400 with field errors, a
    /// missing record a 404, and a clash with something that already exists a 409.
    /// </summary>
    private static IResult Answer(DashboardViewResult result, bool created = false) => result.Outcome switch
    {
        DashboardViewOutcome.Success when created =>
            Results.Created("/api/dashboard", new { result.Layout, result.Views }),
        DashboardViewOutcome.Success => Results.Ok(new { result.Layout, result.Views }),
        DashboardViewOutcome.NameInUse => Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "That name is already taken.",
            detail: "You already have a dashboard view with this name. Views are listed by name, so each one needs its own."),
        DashboardViewOutcome.TooMany => Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["views"] = [$"You can keep up to {DashboardService.MaximumViews} dashboard views. Delete one to add another."],
        }),
        _ => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Dashboard view not found."),
    };

    private static SaveDashboardViewRequest Request(SaveDashboardViewPayload payload) => new(
        payload.Name,
        payload.Placements?
            .Select(item => new DashboardPlacement(
                Enum.Parse<DashboardWidgetType>(item!.Type!, ignoreCase: true),
                Enum.Parse<DashboardWidgetWidth>(item.Width!, ignoreCase: true),
                // Absent means a card, so a caller that has no opinion about the shape need not send one.
                item.Display is null
                    ? DashboardDisplay.Card
                    : Enum.Parse<DashboardDisplay>(item.Display, ignoreCase: true)))
            .ToList());

    /// <summary>
    /// Everything wrong with a proposed view, named per field (CONVENTIONS: validation is a 400 with field
    /// errors).
    /// <para>
    /// The widget and the width arrive as strings and are parsed here rather than bound as enums, because
    /// model binding accepts any integer for an enum — <c>"width": 99</c> would otherwise bind to a member
    /// that does not exist and be stored. That is the hole WP-5.3 found and WP-5.4 restated, met for a
    /// third time; <see cref="Enum.IsDefined{T}(T)"/> is the guard.
    /// </para>
    /// <para>
    /// <b>An empty placement list is allowed</b>, unlike the first cut of this endpoint. A view somebody
    /// names and then fills is exactly what "new view" means, and refusing the empty state would make a
    /// blank slate impossible.
    /// </para>
    /// </summary>
    private static Dictionary<string, string[]> Validate(
        SaveDashboardViewPayload? payload,
        bool nameRequired)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (payload is null)
        {
            errors["view"] = ["A view is required."];
            return errors;
        }

        var name = payload.Name?.Trim();
        if (nameRequired && string.IsNullOrEmpty(name))
        {
            errors["name"] = ["Give this view a name."];
        }
        else if (name is { Length: > DashboardService.MaximumNameLength })
        {
            errors["name"] = [$"A name is at most {DashboardService.MaximumNameLength} characters."];
        }

        var placements = payload.Placements;
        if (placements is null)
        {
            return errors;
        }

        if (placements.Count > DashboardService.MaximumPlacements)
        {
            errors["placements"] =
            [
                $"A view holds at most {DashboardService.MaximumPlacements} widgets — one of each.",
            ];
            return errors;
        }

        var seen = new HashSet<DashboardWidgetType>();
        for (var index = 0; index < placements.Count; index++)
        {
            var placement = placements[index];
            var field = $"placements[{index}]";

            if (placement is null)
            {
                errors[field] = ["A placement must name a widget and a width."];
                continue;
            }

            if (!TryParse<DashboardWidgetType>(placement.Type, out var type))
            {
                errors[$"{field}.type"] =
                [
                    $"'{placement.Type}' is not a widget. Use "
                    + $"{string.Join(", ", Enum.GetNames<DashboardWidgetType>())}.",
                ];
            }
            else if (!seen.Add(type))
            {
                // A widget cannot appear twice: the layout is an ordering of the widgets, and two places
                // for one card is a request with no meaning rather than one to guess at.
                errors[$"{field}.type"] = [$"{type} is placed more than once."];
            }

            if (!TryParse<DashboardWidgetWidth>(placement.Width, out _))
            {
                errors[$"{field}.width"] =
                [
                    $"'{placement.Width}' is not a width. Use "
                    + $"{string.Join(", ", Enum.GetNames<DashboardWidgetWidth>())}.",
                ];
            }

            if (placement.Display is not null && !TryParse<DashboardDisplay>(placement.Display, out _))
            {
                errors[$"{field}.display"] =
                [
                    $"'{placement.Display}' is not a shape. Use "
                    + $"{string.Join(", ", Enum.GetNames<DashboardDisplay>())}.",
                ];
            }
        }

        return errors;
    }

    private static bool TryParse<T>(string? value, out T parsed) where T : struct, Enum =>
        Enum.TryParse(value, ignoreCase: true, out parsed) && Enum.IsDefined(parsed);

    /// <summary>
    /// The request as it arrives: strings, so that an unknown widget is a 400 naming it rather than a
    /// binding failure naming a member number. A null <c>Placements</c> means "leave the cards alone",
    /// which is how a rename travels; an empty one means "this view has no cards".
    /// </summary>
    public sealed record SaveDashboardViewPayload(
        string? Name,
        IReadOnlyList<DashboardPlacementPayload?>? Placements);

    public sealed record DashboardPlacementPayload(string? Type, string? Width, string? Display);
}
