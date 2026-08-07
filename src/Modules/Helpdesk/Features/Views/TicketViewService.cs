using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.EntityFrameworkCore;
using Modules.Helpdesk.Data;
using Modules.Helpdesk.Features.Tickets;
using Platform.Auditing;

namespace Modules.Helpdesk.Features.Views;

public sealed class TicketViewService(HelpdeskDbContext dbContext, IAuditService auditService) : ITicketViewService
{
    internal static readonly JsonSerializerOptions FilterSerializerOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public async Task<IReadOnlyList<TicketViewResponse>> ListAsync(
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        if (IsEndUser(actor))
        {
            return [];
        }

        var actorId = ActorId(actor);
        var views = await dbContext.TicketViews
            .Where(view => view.OwnerId == actorId || view.IsShared)
            .OrderBy(view => view.Name)
            .ToListAsync(cancellationToken);
        return [.. views.Select(view => Map(view, actorId))];
    }

    public async Task<TicketViewResult> CreateAsync(
        SaveTicketViewRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        if (IsEndUser(actor))
        {
            return new(TicketViewOutcome.Forbidden, Error: "Requesters cannot save ticket views.");
        }

        var actorId = ActorId(actor);
        var name = request.Name.Trim();
        if (await NameTakenAsync(actorId, name, null, cancellationToken))
        {
            return new(TicketViewOutcome.DuplicateName, Error: $"You already have a view named '{name}'.");
        }

        var now = DateTimeOffset.UtcNow;
        var view = new TicketView
        {
            Id = Guid.CreateVersion7(),
            Name = name,
            OwnerId = actorId,
            OwnerDisplayName = ActorDisplayName(actor),
            IsShared = request.IsShared,
            FilterJson = Serialize(request.Filter),
            CreatedAt = now,
            UpdatedAt = now,
        };
        dbContext.TicketViews.Add(view);
        await dbContext.SaveChangesAsync(cancellationToken);

        var response = Map(view, actorId);
        await auditService.WriteAsync(
            actor, "Created", "TicketView", view.Id.ToString(), null, response, cancellationToken);
        return new(TicketViewOutcome.Success, response);
    }

    public async Task<TicketViewResult> UpdateAsync(
        Guid id,
        SaveTicketViewRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        if (IsEndUser(actor))
        {
            return new(TicketViewOutcome.Forbidden, Error: "Requesters cannot save ticket views.");
        }

        var actorId = ActorId(actor);
        var view = await dbContext.TicketViews.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (view is null || (!view.IsShared && view.OwnerId != actorId))
        {
            return new(TicketViewOutcome.NotFound);
        }

        if (view.OwnerId != actorId)
        {
            return new(TicketViewOutcome.Forbidden, Error: "Only the owner of a shared view can change it.");
        }

        var name = request.Name.Trim();
        if (await NameTakenAsync(actorId, name, id, cancellationToken))
        {
            return new(TicketViewOutcome.DuplicateName, Error: $"You already have a view named '{name}'.");
        }

        var before = Map(view, actorId);
        view.Name = name;
        view.IsShared = request.IsShared;
        view.FilterJson = Serialize(request.Filter);
        view.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);

        var after = Map(view, actorId);
        await auditService.WriteAsync(
            actor, "Updated", "TicketView", view.Id.ToString(), before, after, cancellationToken);
        return new(TicketViewOutcome.Success, after);
    }

    public async Task<TicketViewOutcome> DeleteAsync(
        Guid id,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        if (IsEndUser(actor))
        {
            return TicketViewOutcome.Forbidden;
        }

        var actorId = ActorId(actor);
        var view = await dbContext.TicketViews.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (view is null || (!view.IsShared && view.OwnerId != actorId))
        {
            return TicketViewOutcome.NotFound;
        }

        if (view.OwnerId != actorId)
        {
            return TicketViewOutcome.Forbidden;
        }

        var before = Map(view, actorId);
        dbContext.TicketViews.Remove(view);
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditService.WriteAsync(actor, "Deleted", "TicketView", id.ToString(), before, null, cancellationToken);
        return TicketViewOutcome.Success;
    }

    private Task<bool> NameTakenAsync(
        string ownerId,
        string name,
        Guid? excludingId,
        CancellationToken cancellationToken) =>
        dbContext.TicketViews.AnyAsync(
            view => view.OwnerId == ownerId && view.Name.ToLower() == name.ToLower() && view.Id != excludingId,
            cancellationToken);

    internal static string Serialize(TicketListFilter filter) =>
        JsonSerializer.Serialize(filter, FilterSerializerOptions);

    internal static TicketListFilter Deserialize(string filterJson)
    {
        try
        {
            return JsonSerializer.Deserialize<TicketListFilter>(filterJson, FilterSerializerOptions)
                ?? TicketListFilter.Empty;
        }
        catch (JsonException)
        {
            return TicketListFilter.Empty;
        }
    }

    private static bool IsEndUser(ClaimsPrincipal actor) => actor.IsInRole("EndUser");
    private static string ActorId(ClaimsPrincipal actor) =>
        actor.FindFirstValue("sub") ?? actor.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException("An authenticated actor identifier is required.");
    private static string ActorDisplayName(ClaimsPrincipal actor) =>
        actor.FindFirstValue("name") ?? actor.Identity?.Name ?? actor.FindFirstValue("preferred_username")
        ?? ActorId(actor);

    private static TicketViewResponse Map(TicketView view, string actorId) => new(
        view.Id,
        view.Name,
        view.OwnerId,
        view.OwnerDisplayName ?? view.OwnerId,
        view.IsShared,
        view.OwnerId == actorId,
        Deserialize(view.FilterJson),
        view.CreatedAt,
        view.UpdatedAt);
}
