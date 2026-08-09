using Modules.Monitoring.Data;
using Modules.Monitoring.Features.PollerConfig;

namespace Infrastructure.Tests;

/// <summary>
/// What a poller learns from a run of config changes. The awkward cases all live here — a device
/// deleted, disabled, moved between groups, or created and destroyed between two fetches — because
/// each one is the difference between a delta that is honest and one that silently keeps polling.
/// </summary>
public sealed class PollerConfigDeltaPlannerTests
{
    private const string Group = "default";
    private const string OtherGroup = "branch";

    [Fact]
    public void Plan_NoChanges_AsksForNothing()
    {
        var delta = PollerConfigDeltaPlanner.Plan(Group, [], new HashSet<Guid>());

        Assert.Empty(delta.ChangedDeviceIds);
        Assert.Empty(delta.RemovedDeviceIds);
    }

    [Fact]
    public void Plan_DeviceEditedAndStillInTheGroup_IsResent()
    {
        var deviceId = Guid.CreateVersion7();

        var delta = PollerConfigDeltaPlanner.Plan(
            Group,
            [Change(deviceId, Group, MonitoringConfigChangeKind.Upserted)],
            new HashSet<Guid> { deviceId });

        Assert.Equal([deviceId], delta.ChangedDeviceIds);
        Assert.Empty(delta.RemovedDeviceIds);
    }

    /// <summary>A check edit is a device change: a device is sent whole or not at all.</summary>
    [Fact]
    public void Plan_CheckEdited_ResendsItsDevice()
    {
        var deviceId = Guid.CreateVersion7();

        var delta = PollerConfigDeltaPlanner.Plan(
            Group,
            [Change(deviceId, Group, MonitoringConfigChangeKind.Upserted, MonitoringConfigEntity.Check)],
            new HashSet<Guid> { deviceId });

        Assert.Equal([deviceId], delta.ChangedDeviceIds);
    }

    [Fact]
    public void Plan_DeviceDeleted_IsReportedRemoved()
    {
        var deviceId = Guid.CreateVersion7();

        var delta = PollerConfigDeltaPlanner.Plan(
            Group,
            [Change(deviceId, Group, MonitoringConfigChangeKind.Removed)],
            new HashSet<Guid>());

        Assert.Empty(delta.ChangedDeviceIds);
        Assert.Equal([deviceId], delta.RemovedDeviceIds);
    }

    /// <summary>
    /// Disabling writes an Upserted change, because the row still exists. What decides the answer is
    /// membership of the poller's live set, not the kind recorded at the time.
    /// </summary>
    [Fact]
    public void Plan_DeviceDisabled_IsReportedRemovedDespiteAnUpsertedChange()
    {
        var deviceId = Guid.CreateVersion7();

        var delta = PollerConfigDeltaPlanner.Plan(
            Group,
            [Change(deviceId, Group, MonitoringConfigChangeKind.Upserted)],
            new HashSet<Guid>());

        Assert.Equal([deviceId], delta.RemovedDeviceIds);
    }

    /// <summary>
    /// A move writes two changes: an upsert against the new group and a removal against the old one.
    /// Both pollers read the same run and each learns the half that concerns it.
    /// </summary>
    [Fact]
    public void Plan_DeviceMovedBetweenGroups_TellsEachPollerItsOwnHalf()
    {
        var deviceId = Guid.CreateVersion7();
        MonitoringConfigChange[] changes =
        [
            Change(deviceId, OtherGroup, MonitoringConfigChangeKind.Upserted),
            Change(deviceId, Group, MonitoringConfigChangeKind.Removed),
        ];

        var leftBehind = PollerConfigDeltaPlanner.Plan(Group, changes, new HashSet<Guid>());
        var newOwner = PollerConfigDeltaPlanner.Plan(OtherGroup, changes, new HashSet<Guid> { deviceId });

        Assert.Equal([deviceId], leftBehind.RemovedDeviceIds);
        Assert.Empty(leftBehind.ChangedDeviceIds);
        Assert.Equal([deviceId], newOwner.ChangedDeviceIds);
        Assert.Empty(newOwner.RemovedDeviceIds);
    }

    [Fact]
    public void Plan_ChangeInAnotherGroup_IsIgnored()
    {
        var deviceId = Guid.CreateVersion7();

        var delta = PollerConfigDeltaPlanner.Plan(
            Group,
            [Change(deviceId, OtherGroup, MonitoringConfigChangeKind.Upserted)],
            new HashSet<Guid>());

        Assert.Empty(delta.ChangedDeviceIds);
        Assert.Empty(delta.RemovedDeviceIds);
    }

    /// <summary>
    /// A device created and deleted between two fetches is touched but absent. Reporting it as
    /// removed is correct and harmless: a poller that never held it ignores the id.
    /// </summary>
    [Fact]
    public void Plan_DeviceCreatedAndDeletedBetweenFetches_IsReportedRemovedOnce()
    {
        var deviceId = Guid.CreateVersion7();

        var delta = PollerConfigDeltaPlanner.Plan(
            Group,
            [
                Change(deviceId, Group, MonitoringConfigChangeKind.Upserted),
                Change(deviceId, Group, MonitoringConfigChangeKind.Removed),
            ],
            new HashSet<Guid>());

        Assert.Equal([deviceId], delta.RemovedDeviceIds);
        Assert.Empty(delta.ChangedDeviceIds);
    }

    [Fact]
    public void Plan_ManyChangesToOneDevice_ResendItOnce()
    {
        var deviceId = Guid.CreateVersion7();

        var delta = PollerConfigDeltaPlanner.Plan(
            Group,
            [
                Change(deviceId, Group, MonitoringConfigChangeKind.Upserted),
                Change(deviceId, Group, MonitoringConfigChangeKind.Upserted, MonitoringConfigEntity.Check),
                Change(deviceId, Group, MonitoringConfigChangeKind.Upserted, MonitoringConfigEntity.Check),
            ],
            new HashSet<Guid> { deviceId });

        Assert.Equal([deviceId], delta.ChangedDeviceIds);
    }

    /// <summary>
    /// A maintenance window carries no device, because windows are always sent in full. Its change
    /// exists only to move the version.
    /// </summary>
    [Fact]
    public void Plan_MaintenanceWindowChange_TouchesNoDevice()
    {
        var delta = PollerConfigDeltaPlanner.Plan(
            Group,
            [
                new MonitoringConfigChange
                {
                    Version = 1,
                    EntityType = MonitoringConfigEntity.MaintenanceWindow,
                    EntityId = Guid.CreateVersion7(),
                    DeviceId = null,
                    PollerGroup = null,
                    Kind = MonitoringConfigChangeKind.Upserted,
                    OccurredAt = DateTimeOffset.UtcNow,
                },
            ],
            new HashSet<Guid>());

        Assert.Empty(delta.ChangedDeviceIds);
        Assert.Empty(delta.RemovedDeviceIds);
    }

    private static MonitoringConfigChange Change(
        Guid deviceId,
        string pollerGroup,
        MonitoringConfigChangeKind kind,
        MonitoringConfigEntity entityType = MonitoringConfigEntity.Device) =>
        new()
        {
            Version = 1,
            EntityType = entityType,
            EntityId = entityType is MonitoringConfigEntity.Device ? deviceId : Guid.CreateVersion7(),
            DeviceId = deviceId,
            PollerGroup = pollerGroup,
            Kind = kind,
            OccurredAt = DateTimeOffset.UtcNow,
        };
}
