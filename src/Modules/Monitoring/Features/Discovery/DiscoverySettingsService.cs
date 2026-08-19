using System.Security.Claims;

using Microsoft.EntityFrameworkCore;
using Modules.Monitoring.Data;
using Platform.Auditing;

namespace Modules.Monitoring.Features.Discovery;

public interface IDiscoverySettingsService
{
    Task<DiscoverySettingsResponse> GetAsync(CancellationToken cancellationToken);

    Task<DiscoverySettingsResponse> UpdateAsync(
        UpdateDiscoverySettingsRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken);
}

/// <summary>
/// The estate-wide discovery switch, read on every scanner config fetch and written by a person.
/// <para>
/// The row is created on first read rather than by a seeder or a migration's insert. A migration that
/// seeded it would be one more applied migration to reason about, and a database restored from before
/// this feature would arrive without the row — so the safe read is the one that makes it.
/// </para>
/// </summary>
public sealed class DiscoverySettingsService(
    MonitoringDbContext dbContext,
    IAuditService auditService) : IDiscoverySettingsService
{
    /// <summary>Who the row belongs to until a person changes it. Never a subject id.</summary>
    private const string SystemActor = "system:monitoring";

    public async Task<DiscoverySettingsResponse> GetAsync(CancellationToken cancellationToken) =>
        Map(await LoadAsync(cancellationToken));

    public async Task<DiscoverySettingsResponse> UpdateAsync(
        UpdateDiscoverySettingsRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var settings = await LoadAsync(cancellationToken);
        var before = Map(settings);

        settings.ScheduledScanningEnabled = request.ScheduledScanningEnabled;
        settings.UpdatedBy = GetActorId(actor);
        settings.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);

        var response = Map(settings);
        await auditService.WriteAsync(
            actor, "Updated", "DiscoverySettings", settings.Id.ToString(), before, response, cancellationToken);
        return response;
    }

    /// <summary>
    /// Get-or-create, and the insert races: two requests arriving together both find nothing. The
    /// loser's insert violates the primary key, so it re-reads rather than surfacing a 500 — the row is
    /// a singleton by key, which is what makes recovering from the race this cheap.
    /// </summary>
    private async Task<DiscoverySettings> LoadAsync(CancellationToken cancellationToken)
    {
        if (await dbContext.DiscoverySettings.FirstOrDefaultAsync(cancellationToken) is { } existing)
        {
            return existing;
        }

        var settings = new DiscoverySettings
        {
            Id = DiscoverySettings.SingletonId,
            ScheduledScanningEnabled = true,
            UpdatedBy = SystemActor,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        dbContext.DiscoverySettings.Add(settings);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return settings;
        }
        catch (DbUpdateException)
        {
            dbContext.Entry(settings).State = EntityState.Detached;
            return await dbContext.DiscoverySettings.FirstAsync(cancellationToken);
        }
    }

    private static DiscoverySettingsResponse Map(DiscoverySettings settings) =>
        new(settings.ScheduledScanningEnabled, settings.UpdatedBy, settings.UpdatedAt);

    private static string GetActorId(ClaimsPrincipal actor) =>
        actor.FindFirstValue("sub") ?? actor.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException("An authenticated actor identifier is required.");
}
