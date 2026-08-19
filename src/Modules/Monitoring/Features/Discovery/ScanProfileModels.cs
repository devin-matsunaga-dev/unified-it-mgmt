using Modules.Monitoring.Features.Devices;

namespace Modules.Monitoring.Features.Discovery;

public sealed record CreateScanProfileRequest(
    string Name,
    IReadOnlyList<string> Ranges,
    string? Description = null,
    string? DiscoveryGroup = null,
    IReadOnlyList<int>? Ports = null,
    int IntervalMinutes = 60,
    int TimeoutSeconds = 2,
    bool SnmpEnabled = true,
    bool NeighbourDiscoveryEnabled = true,
    bool IsEnabled = true,
    bool ScheduleEnabled = true);

/// <summary>
/// A complete statement, like every other update in this module: an omitted port list clears the
/// fingerprint step rather than leaving the previous one in place.
/// </summary>
public sealed record UpdateScanProfileRequest(
    string Name,
    IReadOnlyList<string> Ranges,
    string? Description = null,
    string? DiscoveryGroup = null,
    IReadOnlyList<int>? Ports = null,
    int IntervalMinutes = 60,
    int TimeoutSeconds = 2,
    bool SnmpEnabled = true,
    bool NeighbourDiscoveryEnabled = true,
    bool IsEnabled = true,
    bool ScheduleEnabled = true);

public sealed record ScanProfileListRequest(
    string? Search = null,
    string? DiscoveryGroup = null,
    bool? IsEnabled = null,
    int Page = 1,
    int PageSize = 25);

/// <param name="AddressCount">
/// How many addresses this profile's ranges expand to, so an operator can see that a /16 they typed
/// is 65,534 probes before the scanner tries them. Null for a profile whose ranges include
/// <c>local</c>, whose size is only knowable on the machine that runs it.
/// </param>
public sealed record ScanProfileResponse(
    Guid Id,
    string Name,
    string? Description,
    string DiscoveryGroup,
    IReadOnlyList<string> Ranges,
    IReadOnlyList<int> Ports,
    int IntervalMinutes,
    int TimeoutSeconds,
    bool SnmpEnabled,
    bool NeighbourDiscoveryEnabled,
    bool IsEnabled,
    bool ScheduleEnabled,
    long? AddressCount,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    string UpdatedBy,
    DateTimeOffset UpdatedAt);

public sealed record ScanProfilePageResponse(
    IReadOnlyList<ScanProfileResponse> Items,
    int Total,
    int Page,
    int PageSize);

public sealed record ScanProfileResult(
    MonitoringOutcome Outcome,
    ScanProfileResponse? Profile = null,
    IReadOnlyDictionary<string, string[]>? Errors = null,
    string? Error = null);

/// <summary>
/// What one discovery group has to scan, sent whole on every fetch.
/// <para>
/// No versions and no deltas, unlike the poller's device configuration: an estate has a handful of
/// scan profiles rather than hundreds of devices, and the WP-3.1 reasoning for maintenance windows
/// applies unchanged — a scanner that re-reads a short list costs nothing, while a scanner that
/// misses a change scans the wrong range for an hour.
/// </para>
/// </summary>
/// <param name="ScheduledScanningEnabled">
/// The estate-wide switch, sent on every fetch rather than left for the scanner to ask about
/// separately. With it false the scanner runs nothing on a timer and still collects requested runs —
/// so a scanner that is a version behind and ignores this field keeps scanning, which is the safe way
/// round for a field that turns work off.
/// </param>
public sealed record DiscoveryConfigResponse(
    string DiscoveryGroup,
    IReadOnlyList<DiscoveryScanProfileConfig> Profiles,
    DateTimeOffset GeneratedAt,
    bool ScheduledScanningEnabled = true);

/// <param name="IntervalSeconds">
/// The profile's interval in seconds rather than minutes. The scanner schedules against a monotonic
/// clock in seconds like the poller does, and converting on the way out means the unit is chosen once
/// here rather than in whichever scanner implementation reads this.
/// </param>
/// <param name="ScheduleEnabled">
/// Whether <paramref name="IntervalSeconds"/> means anything for this profile. False makes it
/// on-demand only: it is still sent, so a requested run can name it, but no cycle starts it.
/// </param>
public sealed record DiscoveryScanProfileConfig(
    Guid ScanProfileId,
    string Name,
    IReadOnlyList<string> Ranges,
    IReadOnlyList<int> Ports,
    int IntervalSeconds,
    int TimeoutSeconds,
    bool SnmpEnabled,
    bool NeighbourDiscoveryEnabled,
    bool ScheduleEnabled = true);
