using Modules.Monitoring.Data;

namespace Modules.Monitoring.Features.PollerConfig;

/// <summary>Which devices a delta has to re-send, and which the poller should drop.</summary>
public sealed record PollerConfigDelta(
    IReadOnlyList<Guid> ChangedDeviceIds,
    IReadOnlyList<Guid> RemovedDeviceIds);

/// <summary>
/// Turns a run of config-change rows into the two lists a poller needs. Kept pure and separate from
/// the service so the whole matrix — a device deleted, disabled, moved between groups, or edited and
/// then deleted between two fetches — is unit-testable without a database.
/// </summary>
public static class PollerConfigDeltaPlanner
{
    /// <param name="pollerGroup">The group the asking poller belongs to.</param>
    /// <param name="changes">Every change newer than the version the poller holds, any group.</param>
    /// <param name="devicesInGroup">
    /// The devices that should be polled right now: enabled, and currently in this group. Anything
    /// the changes touched but that is not in here has left the poller's world, whatever the reason.
    /// </param>
    public static PollerConfigDelta Plan(
        string pollerGroup,
        IReadOnlyCollection<MonitoringConfigChange> changes,
        IReadOnlySet<Guid> devicesInGroup)
    {
        var touched = new HashSet<Guid>();
        foreach (var change in changes)
        {
            // A maintenance window carries no device and no group: windows are always sent in full,
            // so such a change only has to move the version.
            if (change.DeviceId is not { } deviceId)
            {
                continue;
            }

            // A device that moved groups wrote an Upserted against its new group and a Removed
            // against its old one, so matching on the recorded group is what makes both pollers
            // learn the right thing from the same run of changes.
            if (!string.Equals(change.PollerGroup, pollerGroup, StringComparison.Ordinal))
            {
                continue;
            }

            touched.Add(deviceId);
        }

        // The change kind says what happened at the time; membership says what is true now. A device
        // created and deleted between two fetches is touched, absent, and correctly reported as
        // removed — a poller that never held it simply ignores the id.
        var changed = new List<Guid>();
        var removed = new List<Guid>();
        foreach (var deviceId in touched.Order())
        {
            (devicesInGroup.Contains(deviceId) ? changed : removed).Add(deviceId);
        }

        return new(changed, removed);
    }
}
