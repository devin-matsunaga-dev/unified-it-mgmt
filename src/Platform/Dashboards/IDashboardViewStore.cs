using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using Platform.Data;

namespace Platform.Dashboards;

/// <summary>
/// Where one person's saved views are kept. A seam rather than a direct <see cref="PlatformDbContext"/> call
/// inside <see cref="DashboardService"/>, for the reason the repo already keeps one behind
/// <c>IAlertStateStore</c>: what the service decides — which widgets are asked, in what order, and what
/// happens when one fails — is worth asserting without a database in the room, and none of it depends on how
/// a view is stored.
/// </summary>
public interface IDashboardViewStore
{
    /// <summary>Every view this owner holds, oldest first, so the tab order does not move under them.</summary>
    Task<IReadOnlyList<StoredDashboardView>> ListAsync(string ownerId, CancellationToken cancellationToken);

    /// <summary>
    /// Writes a new view and makes it the active one, because somebody who creates a view wants to be
    /// looking at it.
    /// </summary>
    Task<StoredDashboardView> CreateAsync(
        string ownerId,
        string name,
        IReadOnlyList<DashboardPlacement> placements,
        CancellationToken cancellationToken);

    /// <summary>
    /// Renames a view, replaces its placements, or both. Null for either means "leave it alone" — so saving
    /// an arrangement does not have to know the name, and renaming does not have to send the cards back.
    /// </summary>
    Task<StoredDashboardView?> UpdateAsync(
        string ownerId,
        Guid id,
        string? name,
        IReadOnlyList<DashboardPlacement>? placements,
        CancellationToken cancellationToken);

    /// <summary>
    /// Removes a view. When it was the active one, the most recently updated survivor takes over — leaving
    /// somebody on no view at all after deleting one would drop them back to the role default without
    /// saying why.
    /// </summary>
    Task<bool> DeleteAsync(string ownerId, Guid id, CancellationToken cancellationToken);

    /// <summary>Makes one view the active one. Null when no such view belongs to this owner.</summary>
    Task<StoredDashboardView?> SelectAsync(string ownerId, Guid id, CancellationToken cancellationToken);

    /// <summary>Whether this owner already has a view of that name, ignoring one they are renaming.</summary>
    Task<bool> NameExistsAsync(
        string ownerId,
        string name,
        Guid? excluding,
        CancellationToken cancellationToken);
}

/// <summary>A view as it came back off the row.</summary>
public sealed record StoredDashboardView(
    Guid Id,
    string Name,
    bool IsActive,
    IReadOnlyList<DashboardPlacement> Placements,
    DateTimeOffset UpdatedAt);

/// <summary>The <c>platform.dashboard_views</c> rows, read and written whole.</summary>
public sealed class DashboardViewStore(
    PlatformDbContext dbContext,
    ILogger<DashboardViewStore> logger) : IDashboardViewStore
{
    /// <summary>
    /// Enum members are written as names, so a stored view stays readable and survives a member being
    /// renumbered. A view outlives several releases — it is the one piece of this feature that is not
    /// recomputed on every read.
    /// </summary>
    private static readonly JsonSerializerOptions LayoutJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public async Task<IReadOnlyList<StoredDashboardView>> ListAsync(
        string ownerId,
        CancellationToken cancellationToken)
    {
        var rows = await dbContext.DashboardViews.AsNoTracking()
            .Where(view => view.OwnerId == ownerId)
            .OrderBy(view => view.CreatedAt).ThenBy(view => view.Id)
            .ToListAsync(cancellationToken);

        return [.. rows.Select(Read)];
    }

    public async Task<StoredDashboardView> CreateAsync(
        string ownerId,
        string name,
        IReadOnlyList<DashboardPlacement> placements,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        await ClearActiveAsync(ownerId, cancellationToken);

        var view = new DashboardView
        {
            Id = Guid.CreateVersion7(),
            OwnerId = ownerId,
            Name = name,
            PlacementsJson = JsonSerializer.Serialize(placements, LayoutJson),
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        dbContext.DashboardViews.Add(view);
        await dbContext.SaveChangesAsync(cancellationToken);
        return Read(view);
    }

    public async Task<StoredDashboardView?> UpdateAsync(
        string ownerId,
        Guid id,
        string? name,
        IReadOnlyList<DashboardPlacement>? placements,
        CancellationToken cancellationToken)
    {
        var view = await FindAsync(ownerId, id, cancellationToken);
        if (view is null)
        {
            return null;
        }

        if (name is not null)
        {
            view.Name = name;
        }

        if (placements is not null)
        {
            view.PlacementsJson = JsonSerializer.Serialize(placements, LayoutJson);
        }

        view.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return Read(view);
    }

    public async Task<bool> DeleteAsync(string ownerId, Guid id, CancellationToken cancellationToken)
    {
        var view = await FindAsync(ownerId, id, cancellationToken);
        if (view is null)
        {
            return false;
        }

        dbContext.DashboardViews.Remove(view);

        if (view.IsActive)
        {
            var successor = await dbContext.DashboardViews
                .Where(other => other.OwnerId == ownerId && other.Id != id)
                .OrderByDescending(other => other.UpdatedAt)
                .FirstOrDefaultAsync(cancellationToken);
            if (successor is not null)
            {
                successor.IsActive = true;
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<StoredDashboardView?> SelectAsync(
        string ownerId,
        Guid id,
        CancellationToken cancellationToken)
    {
        var view = await FindAsync(ownerId, id, cancellationToken);
        if (view is null)
        {
            return null;
        }

        await ClearActiveAsync(ownerId, cancellationToken);
        view.IsActive = true;
        await dbContext.SaveChangesAsync(cancellationToken);
        return Read(view);
    }

    public Task<bool> NameExistsAsync(
        string ownerId,
        string name,
        Guid? excluding,
        CancellationToken cancellationToken) =>
        dbContext.DashboardViews.AsNoTracking()
            .AnyAsync(
                view => view.OwnerId == ownerId
                    && view.Name.ToLower() == name.ToLower()
                    && (excluding == null || view.Id != excluding),
                cancellationToken);

    private Task<DashboardView?> FindAsync(string ownerId, Guid id, CancellationToken cancellationToken) =>
        dbContext.DashboardViews.SingleOrDefaultAsync(
            view => view.Id == id && view.OwnerId == ownerId, cancellationToken);

    /// <summary>
    /// Stands every other view of this owner's down, without saving. The caller stands one up in the same
    /// change, so the "exactly one active" rule is never observable as broken.
    /// </summary>
    private async Task ClearActiveAsync(string ownerId, CancellationToken cancellationToken)
    {
        var active = await dbContext.DashboardViews
            .Where(view => view.OwnerId == ownerId && view.IsActive)
            .ToListAsync(cancellationToken);
        foreach (var view in active)
        {
            view.IsActive = false;
        }
    }

    /// <summary>
    /// A stored view, with unparseable placements read as an empty layout rather than as a failure —
    /// following WP-1.9's saved ticket views, because the alternative is a dashboard that stays broken for
    /// one person until somebody edits a database row by hand.
    /// </summary>
    private StoredDashboardView Read(DashboardView view)
    {
        IReadOnlyList<DashboardPlacement> placements;
        try
        {
            placements = [
                .. (JsonSerializer.Deserialize<List<DashboardPlacement>>(view.PlacementsJson, LayoutJson) ?? [])
                    // A display the enum does not know reads as a card. A view written before the chart
                    // shapes existed has no display at all, and System.Text.Json fills a missing member with
                    // zero rather than with the parameter's default — which is not a member of anything.
                    .Select(placement => Enum.IsDefined(placement.Display)
                        ? placement
                        : placement with { Display = DashboardDisplay.Card }),
            ];
        }
        catch (JsonException exception)
        {
            logger.LogWarning(
                exception, "Dashboard view {View} could not be read; showing it empty.", view.Id);
            placements = [];
        }

        return new StoredDashboardView(view.Id, view.Name, view.IsActive, placements, view.UpdatedAt);
    }
}
