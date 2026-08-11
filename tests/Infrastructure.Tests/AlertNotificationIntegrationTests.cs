using Contracts.Events;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Modules.Monitoring.Data;
using Modules.Monitoring.Features.Alerting;

using Platform.Data;
using Platform.Integration;
using Platform.Notifications;

namespace Infrastructure.Tests;

/// <summary>
/// The monitoring half of WP-3.10: what an alert becomes on its way to a channel. The router is
/// recorded rather than run — its own decisions are covered by
/// <see cref="NotificationRoutingIntegrationTests"/> — so what these assert is the envelope: the
/// severity translation, the device group a rule can be scoped to, and the deep link.
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class AlertNotificationIntegrationTests(InfrastructureFixture infrastructure) : IAsyncLifetime
{
    private const string BaseUrl = "https://it-platform.example.test";

    private MonitoringDbContext _dbContext = null!;
    private RecordingRouter _router = null!;

    public async Task InitializeAsync()
    {
        var options = new DbContextOptionsBuilder<MonitoringDbContext>()
            .UseNpgsql(infrastructure.PostgresConnectionString)
            .Options;
        _dbContext = new MonitoringDbContext(options);
        await _dbContext.Database.MigrateAsync();
        _router = new RecordingRouter();
    }

    public Task DisposeAsync() => _dbContext.DisposeAsync().AsTask();

    /// <summary>
    /// The WP's "Critical alert → Teams message with deep link" from the publishing side: Critical
    /// travels as Critical, the link is absolute and points at the one alert, and the device group is
    /// on the envelope so a group-scoped rule can match it.
    /// </summary>
    [Fact]
    public async Task NotifyRaised_ACriticalAlert_CarriesTheDeepLinkTheGroupAndTheCmdbContext()
    {
        var device = await DeviceAsync("core-network");
        var alert = Raised(device, "Critical");

        await Service().NotifyRaisedAsync(alert, default);

        var envelope = Assert.Single(_router.Routed).Envelope;
        Assert.Equal(nameof(AlertRaised), envelope.EventKind);
        Assert.Equal(NotificationSeverity.Critical, envelope.Severity);
        Assert.Equal($"{BaseUrl}/monitoring/alerts?alertId={alert.AlertId}", envelope.DeepLink);
        Assert.Equal("core-network", envelope.DeviceGroup);
        Assert.Equal($"alert:{device.Id}:{alert.RuleId}:raised", envelope.DedupeKey);
        Assert.Contains(envelope.FactList, fact => fact is { Label: "Check", Value: "ICMP" });
        Assert.Contains(envelope.FactList, fact => fact is { Label: "Owner", Value: "Technician Two" });
        Assert.Contains(envelope.FactList, fact => fact is { Label: "Location", Value: "Primary Data Centre" });
        Assert.Contains(envelope.FactList, fact => fact is { Label: "Device group", Value: "core-network" });
        // An alert is about an asset and the CMDB port answers with an owner's name rather than an id,
        // so there is nobody to address personally: alerts reach channels only.
        Assert.Null(Assert.Single(_router.Routed).UserIds);
    }

    /// <summary>
    /// Found by hand-verification: the poller writes sentences that already name the check, so
    /// prefixing the check name gave "Reachability: Reachability on snmpsim is failing…" on every
    /// message in the estate. The check is still carried, as a fact.
    /// </summary>
    [Fact]
    public async Task NotifyRaised_ASubject_DoesNotRepeatTheCheckNameTheSummaryAlreadyCarries()
    {
        var device = await DeviceAsync("core-network");
        var alert = Raised(device, "Critical") with
        {
            CheckName = "Reachability",
            Summary = "Reachability on snmpsim is failing: no reply after 3 packets.",
        };

        await Service().NotifyRaisedAsync(alert, default);

        var envelope = Assert.Single(_router.Routed).Envelope;
        Assert.Equal("[Critical] Reachability on snmpsim is failing: no reply after 3 packets.", envelope.Subject);
        Assert.DoesNotContain("Reachability: Reachability", envelope.Subject, StringComparison.Ordinal);
    }

    /// <summary>
    /// Also found by hand-verification. An alert row is new on every recurrence, so a dedupe key built
    /// from the alert id meant a digest listed the same failing rule once per occurrence instead of
    /// collapsing it — which is the entire value of a digest. The key is the rule and the device, the
    /// same pair WP-3.6 dedupes its tickets on.
    /// </summary>
    [Fact]
    public async Task NotifyRaised_TwiceForOneRule_CarriesOneDedupeKeySoADigestCanCollapseThem()
    {
        var device = await DeviceAsync("core-network");
        var first = Raised(device, "Critical");
        // A recurrence after a clear: same device, same rule, a brand new alert row.
        var second = Raised(device, "Critical") with { AlertId = Guid.CreateVersion7() };

        await Service().NotifyRaisedAsync(first, default);
        await Service().NotifyRaisedAsync(second, default);

        Assert.NotEqual(first.AlertId, second.AlertId);
        Assert.Equal(_router.Routed[0].Envelope.DedupeKey, _router.Routed[1].Envelope.DedupeKey);
    }

    [Fact]
    public async Task NotifyRaised_AWarningAlert_TravelsAsAWarning()
    {
        var device = await DeviceAsync("edge");

        await Service().NotifyRaisedAsync(Raised(device, "Warning"), default);

        Assert.Equal(NotificationSeverity.Warning, Assert.Single(_router.Routed).Envelope.Severity);
    }

    /// <summary>
    /// A recovery is news, not an emergency. Informational keeps a "Critical only" chat rule a pager
    /// rather than also making it the all-clear.
    /// </summary>
    [Fact]
    public async Task NotifyCleared_AnAlert_IsInformationalAndSaysWhatItWas()
    {
        var device = await DeviceAsync("core-network");
        var alert = new AlertCleared(
            Guid.CreateVersion7(), DateTimeOffset.UtcNow, Guid.CreateVersion7(), device.Id, device.CiId,
            Guid.CreateVersion7(), "check:1:availability", "ICMP", "Critical", "check.success", 1,
            "The device answered again.", DateTimeOffset.UtcNow.AddMinutes(-9), 540);

        await Service().NotifyClearedAsync(alert, default);

        var envelope = Assert.Single(_router.Routed).Envelope;
        Assert.Equal(nameof(AlertCleared), envelope.EventKind);
        Assert.Equal(NotificationSeverity.Informational, envelope.Severity);
        Assert.StartsWith("[Cleared]", envelope.Subject, StringComparison.Ordinal);
        Assert.Contains(envelope.FactList, fact => fact is { Label: "Was", Value: "Critical" });
    }

    /// <summary>
    /// Failure path. Nothing stops a monitored device being deleted out from under an alert, and the
    /// notification still goes — it simply matches no rule that names a group.
    /// </summary>
    [Fact]
    public async Task NotifyRaised_WhenTheDeviceHasBeenDeleted_StillRoutesWithNoGroup()
    {
        var alert = new AlertRaised(
            Guid.CreateVersion7(), DateTimeOffset.UtcNow, Guid.CreateVersion7(), Guid.CreateVersion7(),
            Guid.CreateVersion7(), Guid.CreateVersion7(), "check:1:availability", "ICMP", "Critical",
            "check.success", 0, null, "The device stopped answering.", DateTimeOffset.UtcNow, 3);

        await Service().NotifyRaisedAsync(alert, default);

        var envelope = Assert.Single(_router.Routed).Envelope;
        Assert.Null(envelope.DeviceGroup);
        Assert.Contains(envelope.FactList, fact => fact is { Label: "Device group", Value: "unknown" });
    }

    /// <summary>
    /// With no configured base URL there is no honest absolute link, and a relative one in a chat
    /// message goes nowhere. The message still goes; it just carries no button.
    /// </summary>
    [Fact]
    public async Task NotifyRaised_WithNoConfiguredBaseUrl_CarriesNoDeepLink()
    {
        var device = await DeviceAsync("core-network");

        await Service(baseUrl: null).NotifyRaisedAsync(Raised(device, "Critical"), default);

        Assert.Null(Assert.Single(_router.Routed).Envelope.DeepLink);
    }

    private AlertNotificationService Service(string? baseUrl = BaseUrl) => new(
        _dbContext,
        new StubEnrichment(),
        _router,
        Options.Create(new NotificationOptions { DeepLinkBaseUrl = baseUrl }),
        NullLogger<AlertNotificationService>.Instance);

    private static AlertRaised Raised(MonitoredDevice device, string severity) => new(
        Guid.CreateVersion7(), DateTimeOffset.UtcNow, Guid.CreateVersion7(), device.Id, device.CiId,
        Guid.CreateVersion7(), "check:1:availability", "ICMP", severity, "check.success", 0, null,
        "The device stopped answering.", DateTimeOffset.UtcNow, 3);

    private async Task<MonitoredDevice> DeviceAsync(string pollerGroup)
    {
        var device = new MonitoredDevice
        {
            Id = Guid.CreateVersion7(),
            CiId = Guid.CreateVersion7(),
            Address = $"10.60.{Random.Shared.Next(0, 255)}.{Random.Shared.Next(1, 255)}",
            PollerGroup = pollerGroup,
            IsEnabled = true,
            CreatedBy = "test",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedBy = "test",
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        _dbContext.MonitoredDevices.Add(device);
        await _dbContext.SaveChangesAsync();
        return device;
    }

    /// <summary>The WP-3.7 context, answered without Assets or Helpdesk: this is not their test.</summary>
    private sealed class StubEnrichment : IAlertEnrichmentService
    {
        public Task<AlertCmdbContext> DescribeAsync(Guid ciId, CancellationToken cancellationToken) =>
            Task.FromResult(new AlertCmdbContext(
                ciId, CiFound: true, "dc1-core-sw-01", "NetworkDevice", "AST-0042", "Deployed",
                "Technician Two", "Primary Data Centre", "Information Technology",
                new DateOnly(2028, 5, 12), "Active", 640, "Dell ProSupport", []));

        public Task<IReadOnlyDictionary<Guid, AlertCmdbSummary>> SummariseAsync(
            IReadOnlyCollection<Guid> ciIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, AlertCmdbSummary>>(
                new Dictionary<Guid, AlertCmdbSummary>());
    }

    private sealed class RecordingRouter : INotificationRouter
    {
        public List<(NotificationEnvelope Envelope, IReadOnlyCollection<string>? UserIds)> Routed { get; } = [];

        public Task<NotificationRoutingReport> RouteAsync(
            NotificationEnvelope envelope,
            IReadOnlyCollection<string>? userIds,
            CancellationToken cancellationToken)
        {
            Routed.Add((envelope, userIds));
            return Task.FromResult(new NotificationRoutingReport(1, 0, 0, 0));
        }
    }
}
