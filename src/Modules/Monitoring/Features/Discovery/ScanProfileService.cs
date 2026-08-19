using System.Security.Claims;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Modules.Monitoring.Data;
using Modules.Monitoring.Features.Devices;
using Platform.Auditing;

namespace Modules.Monitoring.Features.Discovery;

public sealed class ScanProfileService(
    MonitoringDbContext dbContext,
    IAuditService auditService,
    IDiscoverySettingsService settingsService) : IScanProfileService
{
    /// <summary>Where a profile lands when the caller does not say which scanner runs it.</summary>
    public const string DefaultDiscoveryGroup = "default";

    private const int MaximumPageSize = 200;

    /// <summary>
    /// Deliberately no config log. WP-3.1's <c>monitoring.config_changes</c> exists so a poller can be
    /// handed a delta of two hundred devices without re-reading them all; a discovery group has a
    /// handful of profiles and is sent the list whole, so a version history would be a table nobody
    /// reads and a second thing to keep consistent.
    /// </summary>
    public async Task<ScanProfilePageResponse> ListAsync(
        ScanProfileListRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var page = Math.Max(request.Page, 1);
        var pageSize = Math.Clamp(request.PageSize, 1, MaximumPageSize);

        var query = dbContext.ScanProfiles.AsNoTracking();
        if (request.IsEnabled is { } isEnabled)
        {
            query = query.Where(profile => profile.IsEnabled == isEnabled);
        }

        if (!string.IsNullOrWhiteSpace(request.DiscoveryGroup))
        {
            query = query.Where(profile => profile.DiscoveryGroup == request.DiscoveryGroup);
        }

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var term = $"%{request.Search.Trim()}%";
            query = query.Where(profile => EF.Functions.ILike(profile.Name, term));
        }

        var total = await query.CountAsync(cancellationToken);
        var profiles = await query
            .OrderBy(profile => profile.Name).ThenBy(profile => profile.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new([.. profiles.Select(Map)], total, page, pageSize);
    }

    public async Task<ScanProfileResponse?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        await dbContext.ScanProfiles.AsNoTracking()
            .SingleOrDefaultAsync(profile => profile.Id == id, cancellationToken) is { } profile
            ? Map(profile)
            : null;

    public async Task<ScanProfileResult> CreateAsync(
        CreateScanProfileRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var errors = ScanProfileRules.Validate(
            request.Ranges, request.Ports, request.IntervalMinutes, request.TimeoutSeconds);
        if (errors.Count > 0)
        {
            return new(MonitoringOutcome.Invalid, Errors: errors);
        }

        var name = request.Name.Trim();
        if (await dbContext.ScanProfiles.AnyAsync(profile => profile.Name == name, cancellationToken))
        {
            return new(MonitoringOutcome.Duplicate, Error: $"A scan profile named '{name}' already exists.");
        }

        var actorId = GetActorId(actor);
        var now = DateTimeOffset.UtcNow;
        var profile = new ScanProfile
        {
            Id = Guid.CreateVersion7(),
            Name = name,
            Description = Trimmed(request.Description),
            DiscoveryGroup = NormaliseGroup(request.DiscoveryGroup),
            RangesJson = SerializeRanges(request.Ranges),
            PortsJson = SerializePorts(request.Ports),
            IntervalMinutes = request.IntervalMinutes,
            TimeoutSeconds = request.TimeoutSeconds,
            SnmpEnabled = request.SnmpEnabled,
            NeighbourDiscoveryEnabled = request.NeighbourDiscoveryEnabled,
            IsEnabled = request.IsEnabled,
            ScheduleEnabled = request.ScheduleEnabled,
            CreatedBy = actorId,
            CreatedAt = now,
            UpdatedBy = actorId,
            UpdatedAt = now,
        };

        dbContext.ScanProfiles.Add(profile);
        await dbContext.SaveChangesAsync(cancellationToken);

        var response = Map(profile);
        await auditService.WriteAsync(
            actor, "Created", "ScanProfile", profile.Id.ToString(), null, response, cancellationToken);
        return new(MonitoringOutcome.Success, response);
    }

    public async Task<ScanProfileResult> UpdateAsync(
        Guid id,
        UpdateScanProfileRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var profile = await dbContext.ScanProfiles.SingleOrDefaultAsync(
            item => item.Id == id, cancellationToken);
        if (profile is null)
        {
            return new(MonitoringOutcome.NotFound);
        }

        var errors = ScanProfileRules.Validate(
            request.Ranges, request.Ports, request.IntervalMinutes, request.TimeoutSeconds);
        if (errors.Count > 0)
        {
            return new(MonitoringOutcome.Invalid, Errors: errors);
        }

        var name = request.Name.Trim();
        if (await dbContext.ScanProfiles.AnyAsync(
                item => item.Name == name && item.Id != id, cancellationToken))
        {
            return new(MonitoringOutcome.Duplicate, Error: $"A scan profile named '{name}' already exists.");
        }

        var before = Map(profile);
        profile.Name = name;
        profile.Description = Trimmed(request.Description);
        profile.DiscoveryGroup = NormaliseGroup(request.DiscoveryGroup);
        profile.RangesJson = SerializeRanges(request.Ranges);
        profile.PortsJson = SerializePorts(request.Ports);
        profile.IntervalMinutes = request.IntervalMinutes;
        profile.TimeoutSeconds = request.TimeoutSeconds;
        profile.SnmpEnabled = request.SnmpEnabled;
        profile.NeighbourDiscoveryEnabled = request.NeighbourDiscoveryEnabled;
        profile.IsEnabled = request.IsEnabled;
        profile.ScheduleEnabled = request.ScheduleEnabled;
        profile.UpdatedBy = GetActorId(actor);
        profile.UpdatedAt = DateTimeOffset.UtcNow;

        await dbContext.SaveChangesAsync(cancellationToken);

        var response = Map(profile);
        await auditService.WriteAsync(
            actor, "Updated", "ScanProfile", profile.Id.ToString(), before, response, cancellationToken);
        return new(MonitoringOutcome.Success, response);
    }

    public async Task<MonitoringOutcome> DeleteAsync(
        Guid id,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        var profile = await dbContext.ScanProfiles.SingleOrDefaultAsync(
            item => item.Id == id, cancellationToken);
        if (profile is null)
        {
            return MonitoringOutcome.NotFound;
        }

        var before = Map(profile);
        dbContext.ScanProfiles.Remove(profile);
        await dbContext.SaveChangesAsync(cancellationToken);

        await auditService.WriteAsync(
            actor, "Deleted", "ScanProfile", id.ToString(), before, null, cancellationToken);
        return MonitoringOutcome.Success;
    }

    public async Task<DiscoveryConfigResponse> GetConfigAsync(
        string discoveryGroup,
        CancellationToken cancellationToken)
    {
        var group = NormaliseGroup(discoveryGroup);
        var profiles = await dbContext.ScanProfiles.AsNoTracking()
            .Where(profile => profile.DiscoveryGroup == group && profile.IsEnabled)
            .OrderBy(profile => profile.Name).ThenBy(profile => profile.Id)
            .ToListAsync(cancellationToken);

        // A profile whose schedule is off is still sent. It has to be: an on-demand run names a profile
        // the scanner must already hold, and filtering it out here would make "scan now" work only for
        // the profiles that did not need it.
        var settings = await settingsService.GetAsync(cancellationToken);

        // An unknown group is an empty configuration rather than a 404. A scanner is deployed before
        // anybody writes it a profile, and answering 404 would make "nothing to scan yet" and "this
        // platform has never heard of you" the same message on its first cycle.
        return new(
            group,
            [.. profiles.Select(ToConfig)],
            DateTimeOffset.UtcNow,
            settings.ScheduledScanningEnabled);
    }

    /// <summary>
    /// One profile as the scanner reads it. Shared by the config fetch and the dispatch of a requested
    /// run, so that a profile means the same thing however the scanner came to hear about it.
    /// </summary>
    internal static DiscoveryScanProfileConfig ToConfig(ScanProfile profile) =>
        new(profile.Id,
            profile.Name,
            DeserializeRanges(profile.RangesJson),
            DeserializePorts(profile.PortsJson),
            profile.IntervalMinutes * 60,
            profile.TimeoutSeconds,
            profile.SnmpEnabled,
            profile.NeighbourDiscoveryEnabled,
            profile.ScheduleEnabled);

    internal static ScanProfileResponse Map(ScanProfile profile)
    {
        var ranges = DeserializeRanges(profile.RangesJson);
        return new(
            profile.Id,
            profile.Name,
            profile.Description,
            profile.DiscoveryGroup,
            ranges,
            DeserializePorts(profile.PortsJson),
            profile.IntervalMinutes,
            profile.TimeoutSeconds,
            profile.SnmpEnabled,
            profile.NeighbourDiscoveryEnabled,
            profile.IsEnabled,
            profile.ScheduleEnabled,
            // A stored range was validated on the way in, so anything unparseable here is a row edited
            // behind the API's back. It reads as "unknown size" rather than throwing on a list request.
            ScanProfileRules.Parse(ranges) is { } parsed ? ScanRange.TotalAddresses(parsed) : null,
            profile.CreatedBy,
            profile.CreatedAt,
            profile.UpdatedBy,
            profile.UpdatedAt);
    }

    private static string NormaliseGroup(string? discoveryGroup) =>
        string.IsNullOrWhiteSpace(discoveryGroup) ? DefaultDiscoveryGroup : discoveryGroup.Trim();

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string SerializeRanges(IReadOnlyList<string> ranges) =>
        JsonSerializer.Serialize(ranges.Select(range => range.Trim()).ToList());

    private static string SerializePorts(IReadOnlyList<int>? ports) =>
        JsonSerializer.Serialize(ports is null ? [] : ports.ToList());

    internal static IReadOnlyList<string> DeserializeRanges(string rangesJson) =>
        JsonSerializer.Deserialize<List<string>>(rangesJson) ?? [];

    internal static IReadOnlyList<int> DeserializePorts(string portsJson) =>
        JsonSerializer.Deserialize<List<int>>(portsJson) ?? [];

    private static string GetActorId(ClaimsPrincipal actor) =>
        actor.FindFirstValue("sub") ?? actor.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException("An authenticated actor identifier is required.");
}
