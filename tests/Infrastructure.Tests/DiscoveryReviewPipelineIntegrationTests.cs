using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;

using Contracts.Events;

using MassTransit;
using MassTransit.EntityFrameworkCoreIntegration;
using MassTransit.Serialization;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Modules.Assets.Data;
using Modules.Assets.Features.Cis;
using Modules.Assets.Features.Discovery;
using Modules.Monitoring.Data;
using Modules.Monitoring.Features.Devices;
using Modules.Monitoring.Features.Discovery;

using Platform.Data;

namespace Infrastructure.Tests;

/// <summary>
/// WP-4.2 end to end against real infrastructure: a <see cref="DeviceDiscovered"/> arrives, the ladder
/// either places it against a CI or files it for review, a human approves or rejects the card, and the
/// approval reaches Monitoring.
/// <para>
/// The host migrates <c>MonitoringDbContext</c> as well as its own two, because the match ladder's top
/// rung reads Monitoring through a port — the WP-3.6/WP-3.9 port trap, which STATUS records as having
/// cost three packages a run of 500s each. A host that reads another module through a port needs that
/// module's schema.
/// </para>
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class DiscoveryReviewPipelineIntegrationTests : IAsyncLifetime
{
    private readonly DiscoveryReviewApplication _application;
    private HttpClient? _client;

    public DiscoveryReviewPipelineIntegrationTests(InfrastructureFixture infrastructure)
    {
        _application = new DiscoveryReviewApplication(
            infrastructure.PostgresConnectionString,
            infrastructure.RabbitMqConnectionString,
            infrastructure.MinioConnectionString);
    }

    public async Task InitializeAsync()
    {
        _client = _application.CreateClient();
        _client.DefaultRequestHeaders.Add(
            DiscoveryReviewAuthenticationHandler.RoleHeader, "Technician");
        await using var scope = _application.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<PlatformDbContext>().Database.MigrateAsync();
        await scope.ServiceProvider.GetRequiredService<AssetsDbContext>().Database.MigrateAsync();
        await scope.ServiceProvider.GetRequiredService<MonitoringDbContext>().Database.MigrateAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// The contract guard STATUS asked this package to run first: the consumer reads WP-4.1's committed
    /// envelope with MassTransit's own serializer options, so the very first thing that ever consumes a
    /// scanner's message does it against the bytes the scanner actually writes rather than against a
    /// shape invented on this side. <see cref="DiscoveryEnvelopeTests"/> guards the envelope; this
    /// guards that the ingest can read what is inside it.
    /// </summary>
    [Fact]
    public async Task Ingest_TheScannersOwnCommittedEnvelope_IsReadFieldByFieldAndQueued()
    {
        var discovery = FixtureDiscovery();

        var result = await IngestAsync(discovery);

        var card = await GetAsync<DiscoveredDeviceDto>($"/api/discovered-devices/{result.DiscoveredDeviceId}");
        Assert.Equal("Pending", card.Status);
        Assert.Equal("172.18.0.7", card.Address);
        Assert.Equal("sim-switch-healthy.example.test", card.Hostname);
        Assert.True(card.RespondedToPing);
        Assert.Equal([22, 161], card.OpenPorts);
        Assert.Equal("sim-switch-healthy", card.Snmp!.SysName);
        // The numeric form WP-4.1's hand-verification had to fix: prettyPrint resolved this against
        // whatever MIBs sat beside the scanner, and a key that renders two ways is not a key.
        Assert.Equal("1.3.6.1.4.1.8072.3.2.10", card.Snmp.SysObjectId);
        Assert.Equal(2, card.Neighbours.Count);
        Assert.Equal("dc1-core-rtr-01", card.Neighbours[0].RemoteSystemName);
        Assert.Equal("cdp", card.Neighbours[1].Protocol);
        // A device that reports neighbours is network equipment; nothing else runs LLDP or CDP.
        Assert.Equal(nameof(CiType.NetworkDevice), card.SuggestedType);
        Assert.Equal("sim-switch-healthy", card.SuggestedName);
        Assert.Equal("172.18.0.7", card.SuggestedAttributes["managementIp"]);
    }

    /// <summary>
    /// A LAN with no PTR records names nothing over DNS, which is the ordinary case on a home or
    /// small-office network — so a scan asks mDNS and NetBIOS as well, and the card has to say which
    /// answered. The three are not equally trustworthy and an approver is trusting one of them.
    /// </summary>
    [Fact]
    public async Task Discovery_NamedByAProtocolOtherThanDns_KeepsBothTheNameAndHowItWasLearned()
    {
        var address = NewAddress();

        var result = await IngestAsync(
            Discovery(address, hostname: "DESKTOP-7F2K", hostnameSource: "netbios"));

        var queued = await GetAsync<DiscoveredDeviceDto>($"/api/discovered-devices/{result.DiscoveredDeviceId}");
        Assert.Equal("DESKTOP-7F2K", queued.Hostname);
        Assert.Equal("netbios", queued.HostnameSource);

        // And the name is what the card is titled with, which is the whole point of asking. It is
        // lower-cased by `DiscoveryIdentity.ShortHostname`, which every name in this pipeline goes
        // through so that matching a discovery to a CI is case-insensitive — NetBIOS shouting its
        // names in capitals is exactly the case that normalisation exists for.
        Assert.Equal("desktop-7f2k", queued.SuggestedName);
    }

    [Fact]
    public async Task Discovery_ThatNothingCouldName_CarriesNoSourceAndFallsBackToItsAddress()
    {
        var address = NewAddress();

        var result = await IngestAsync(Discovery(address));

        var queued = await GetAsync<DiscoveredDeviceDto>($"/api/discovered-devices/{result.DiscoveredDeviceId}");
        Assert.Null(queued.Hostname);
        Assert.Null(queued.HostnameSource);
        Assert.Equal(address, queued.SuggestedName);
    }

    /// <summary>
    /// The WP's first verification, on the strongest rung: something the platform already polls is
    /// recognised rather than offered up as a stranger, and its CI gets the last-seen and the reported
    /// description the WP asks discovery to keep current.
    /// </summary>
    [Fact]
    public async Task Discovery_OfAnAddressAlreadyMonitored_UpdatesTheCiAndQueuesNothing()
    {
        var address = NewAddress();
        var ciId = await CreateNetworkCiAsync(NewName("monitored"), "10.99.0.1");
        await CreateMonitoredDeviceAsync(ciId, address);

        var result = await IngestAsync(Discovery(address, sysName: NewName("sim"), sysDescription: "Firmware 12.4(25)"));

        Assert.Equal(ciId, result.CiId);
        Assert.Equal(nameof(DiscoveredDeviceStatus.Matched), result.Status.ToString());
        Assert.Equal(DiscoveryMatchRule.MonitoredAddress, result.Rule);

        // Nothing lands on the review queue, because nothing here needs a human.
        var queue = await GetAsync<DiscoveredDevicePageDto>("/api/discovered-devices");
        Assert.DoesNotContain(queue.Items, item => item.Address == address);

        var facts = await GetAsync<CiDiscoveryFactsDto>($"/api/cis/{ciId}/discovery-facts");
        Assert.Equal(address, facts.Address);
        Assert.Equal("Firmware 12.4(25)", facts.Snmp!.SysDescription);
        Assert.Equal(1, facts.SightingCount);
    }

    /// <summary>
    /// The same on a CMDB rung, and the "no dupe" half: a second scan of a matched device moves the
    /// counters and creates neither a second CI nor a review card.
    /// </summary>
    [Fact]
    public async Task Discovery_OfARecordedManagementIpTwice_UpdatesOneCiAndCreatesNoDuplicate()
    {
        var address = NewAddress();
        var ciId = await CreateNetworkCiAsync(NewName("recorded"), address);

        var first = await IngestAsync(Discovery(address, sysDescription: "Firmware 1.0"));
        var second = await IngestAsync(Discovery(address, sysDescription: "Firmware 2.0"));

        Assert.Equal(ciId, first.CiId);
        Assert.Equal(DiscoveryMatchRule.ManagementIp, first.Rule);
        Assert.Equal(first.DiscoveredDeviceId, second.DiscoveredDeviceId);
        Assert.False(second.IsNew);

        var facts = await GetAsync<CiDiscoveryFactsDto>($"/api/cis/{ciId}/discovery-facts");
        Assert.Equal("Firmware 2.0", facts.Snmp!.SysDescription);
        Assert.Equal(2, facts.SightingCount);

        await using var scope = _application.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AssetsDbContext>();
        Assert.Equal(1, await dbContext.Cis.CountAsync(ci => ci.Id == ciId));
        Assert.Equal(1, await dbContext.DiscoveredDevices.CountAsync(row => row.Address == address));
    }

    /// <summary>
    /// The CMDB is never written by a scan. A matched CI keeps the attributes an operator typed, and
    /// the scanned values live beside it — which is precisely the difference WP-4.6's drift report is
    /// built to find, and would not exist if this package overwrote them.
    /// </summary>
    [Fact]
    public async Task Discovery_MatchingACi_LeavesEveryAttributeAnOperatorTyped_Untouched()
    {
        var address = NewAddress();
        var name = NewName("untouched");
        var ciId = await CreateNetworkCiAsync(name, address, vendor: "Cisco", portCount: 24);

        await IngestAsync(Discovery(
            address, sysName: "renamed-by-somebody", sysDescription: "Juniper Networks, Inc. ex2200"));

        var ci = await GetAsync<CiDto>($"/api/cis/{ciId}");
        Assert.Equal(name, ci.Name);
        Assert.Equal("Cisco", ci.Attributes["vendor"]);
        Assert.Equal("24", ci.Attributes["portCount"]);

        var facts = await GetAsync<CiDiscoveryFactsDto>($"/api/cis/{ciId}/discovery-facts");
        Assert.Equal("renamed-by-somebody", facts.Snmp!.SysName);
        Assert.Equal("Juniper Networks, Inc. ex2200", facts.Snmp.SysDescription);
    }

    /// <summary>
    /// The WP's second verification: an unknown device becomes a card, approving it creates the CI and
    /// the monitored device, and the card is settled afterwards.
    /// </summary>
    [Fact]
    public async Task Approve_AnUnknownDevice_CreatesTheCiAndEnrollsItForMonitoring()
    {
        var address = NewAddress();
        var sysName = NewName("stranger");
        var ingest = await IngestAsync(Discovery(address, sysName: sysName));
        Assert.Equal(nameof(DiscoveredDeviceStatus.Pending), ingest.Status.ToString());
        Assert.Null(ingest.CiId);

        var approved = await PostAsync<DiscoveredDeviceDto>(
            $"/api/discovered-devices/{ingest.DiscoveredDeviceId}/approvals",
            new
            {
                type = nameof(CiType.NetworkDevice),
                attributes = new Dictionary<string, string> { ["vendor"] = "Cisco", ["portCount"] = "48" },
                enrollMonitoring = true,
                pollerGroup = "default",
                note = "Confirmed on the rack diagram.",
            });

        Assert.Equal("Approved", approved.Status);
        Assert.NotNull(approved.CiId);
        Assert.Equal("discovery-test-user", approved.ReviewedBy);

        // The CI exists, and it carries what discovery observed plus what the approver supplied.
        var ci = await GetAsync<CiDto>($"/api/cis/{approved.CiId}");
        Assert.Equal(sysName, ci.Name);
        Assert.Equal(address, ci.Attributes["managementIp"]);
        Assert.Equal("Cisco", ci.Attributes["vendor"]);

        // The monitoring half crosses the module boundary as an event, so the enrolment is driven here
        // the way the consumer drives it. What is asserted is that the device and its reachability
        // check exist — a device with no checks is polled by nobody.
        await EnrollAsync(new DiscoveredDeviceApproved(
            Guid.CreateVersion7(), DateTimeOffset.UtcNow, ingest.DiscoveredDeviceId, approved.CiId!.Value,
            address, null, MonitoringRequested: true, PollerGroup: "default"));

        await using var scope = _application.Services.CreateAsyncScope();
        var monitoring = scope.ServiceProvider.GetRequiredService<MonitoringDbContext>();
        var device = await monitoring.MonitoredDevices.Include(item => item.Checks)
            .SingleAsync(item => item.CiId == approved.CiId);
        Assert.Equal(address, device.Address);
        Assert.Equal("default", device.PollerGroup);
        Assert.True(device.IsEnabled);
        var check = Assert.Single(device.Checks);
        Assert.Equal(CheckType.Icmp, check.Type);
        Assert.Equal("Reachability", check.Name);
    }

    /// <summary>
    /// Approving without asking for monitoring creates the CI alone. Discovering something is not a
    /// decision to watch it, and an approval that enrolled every printer would fill the alert board.
    /// </summary>
    [Fact]
    public async Task Approve_WithoutRequestingMonitoring_EnrollsNothing()
    {
        var address = NewAddress();
        var ingest = await IngestAsync(Discovery(address, sysName: NewName("inventory")));

        var approved = await PostAsync<DiscoveredDeviceDto>(
            $"/api/discovered-devices/{ingest.DiscoveredDeviceId}/approvals",
            new
            {
                type = nameof(CiType.NetworkDevice),
                attributes = new Dictionary<string, string> { ["vendor"] = "Aruba", ["portCount"] = "8" },
                enrollMonitoring = false,
            });

        await using var scope = _application.Services.CreateAsyncScope();

        // The event is published either way — the CI is a fact regardless — and it is the flag on it
        // that the consumer returns on. So what is asserted is the flag that reaches the bus.
        var platform = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var published = await platform.Set<OutboxMessage>()
            .Where(message => message.MessageType.Contains(nameof(DiscoveredDeviceApproved)))
            .ToListAsync();
        var body = Assert.Single(
            published.Select(message => JsonDocument.Parse(message.Body).RootElement.GetProperty("message")),
            message => message.GetProperty("ciId").GetGuid() == approved.CiId!.Value);
        Assert.False(body.GetProperty("monitoringRequested").GetBoolean());
        Assert.Equal(address, body.GetProperty("address").GetString());

        var monitoring = scope.ServiceProvider.GetRequiredService<MonitoringDbContext>();
        Assert.False(await monitoring.MonitoredDevices.AnyAsync(item => item.CiId == approved.CiId));
    }

    /// <summary>
    /// Invariant §7.1: every write endpoint produces an audit entry. Approving and rejecting are the
    /// two decisions in this package that a person makes, and both are recorded with before and after.
    /// </summary>
    [Fact]
    public async Task ApproveAndReject_EachWriteAnAuditEntryNamingTheActor()
    {
        var approvedIngest = await IngestAsync(Discovery(NewAddress(), sysName: NewName("audited-a")));
        await PostAsync<DiscoveredDeviceDto>(
            $"/api/discovered-devices/{approvedIngest.DiscoveredDeviceId}/approvals",
            new
            {
                type = nameof(CiType.NetworkDevice),
                attributes = new Dictionary<string, string> { ["vendor"] = "Cisco", ["portCount"] = "8" },
            });
        var rejectedIngest = await IngestAsync(Discovery(NewAddress(), sysName: NewName("audited-r")));
        await PostAsync<DiscoveredDeviceDto>(
            $"/api/discovered-devices/{rejectedIngest.DiscoveredDeviceId}/rejections", new { note = "Not ours." });

        await using var scope = _application.Services.CreateAsyncScope();
        var platform = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var entries = await platform.AuditEntries
            .Where(entry => entry.EntityType == "DiscoveredDevice")
            .ToListAsync();

        Assert.Contains(entries, entry =>
            entry.Action == "Approved" && entry.EntityId == approvedIngest.DiscoveredDeviceId.ToString());
        Assert.Contains(entries, entry =>
            entry.Action == "Rejected" && entry.EntityId == rejectedIngest.DiscoveredDeviceId.ToString());
    }

    /// <summary>
    /// The WP's third verification, and the reason a rejected row is never deleted: the ignore list has
    /// to survive every later scan of the same thing.
    /// </summary>
    [Fact]
    public async Task Reject_ADiscovery_KeepsItOutOfTheQueueOnEveryLaterScan()
    {
        var address = NewAddress();
        var sysName = NewName("printer");
        var ingest = await IngestAsync(Discovery(address, sysName: sysName));

        var rejected = await PostAsync<DiscoveredDeviceDto>(
            $"/api/discovered-devices/{ingest.DiscoveredDeviceId}/rejections",
            new { note = "Contractor's printer; not ours." });
        Assert.Equal("Rejected", rejected.Status);

        // Three more scans find it, exactly as they would on a real estate.
        for (var pass = 0; pass < 3; pass++)
        {
            var again = await IngestAsync(Discovery(address, sysName: sysName));
            Assert.Equal(ingest.DiscoveredDeviceId, again.DiscoveredDeviceId);
            Assert.Equal(nameof(DiscoveredDeviceStatus.Rejected), again.Status.ToString());
            Assert.Null(again.CiId);
        }

        var queue = await GetAsync<DiscoveredDevicePageDto>("/api/discovered-devices");
        Assert.DoesNotContain(queue.Items, item => item.Address == address);

        // Still visible when asked for explicitly: an ignore list nobody can read is one nobody can
        // undo. The sighting counter proves the thing is still out there.
        var all = await GetAsync<DiscoveredDevicePageDto>($"/api/discovered-devices?status=Rejected&search={address}");
        var row = Assert.Single(all.Items);
        Assert.Equal(4, row.SightingCount);
        Assert.Equal("Contractor's printer; not ours.", row.ReviewNote);
    }

    /// <summary>
    /// The tier-migration case, which is the one way a ledger keyed on a best-available identity can
    /// quietly duplicate: a device found first by ping alone and later by SNMP changes which key it
    /// files under, and must still be one card.
    /// </summary>
    [Fact]
    public async Task Discovery_ThatGainsAnSnmpAgentBetweenScans_StaysOneCardAndKeepsItsDecision()
    {
        var address = NewAddress();
        var pingOnly = await IngestAsync(Discovery(address, sysName: null));
        Assert.Equal(nameof(DiscoveredDeviceStatus.Pending), pingOnly.Status.ToString());

        var withSnmp = await IngestAsync(Discovery(address, sysName: NewName("nowspeaking")));

        Assert.Equal(pingOnly.DiscoveredDeviceId, withSnmp.DiscoveredDeviceId);
        Assert.False(withSnmp.IsNew);

        await using var scope = _application.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AssetsDbContext>();
        var row = await dbContext.DiscoveredDevices.SingleAsync(item => item.Id == pingOnly.DiscoveredDeviceId);
        // The key was rewritten in place to the stronger tier rather than a second row being written.
        Assert.StartsWith("snmp:", row.IdentityKey, StringComparison.Ordinal);
        Assert.Equal(2, row.SightingCount);
    }

    /// <summary>
    /// A DHCP lease moving is not two devices becoming one. A row whose sysName contradicts the new
    /// sighting is a different device that has since been handed the same address, and merging them
    /// would rewrite one device's history with another's — including its review decision.
    /// </summary>
    [Fact]
    public async Task Discovery_OfADifferentDeviceAtARecycledAddress_IsASecondCard()
    {
        var address = NewAddress();
        var first = await IngestAsync(Discovery(address, sysName: NewName("tenant-a")));

        var second = await IngestAsync(Discovery(address, sysName: NewName("tenant-b")));

        Assert.NotEqual(first.DiscoveredDeviceId, second.DiscoveredDeviceId);
        Assert.True(second.IsNew);
    }

    /// <summary>
    /// Two CIs recording one management IP is a contradiction in the estate. The card says so and names
    /// them, rather than the platform picking one and being quietly wrong about every later scan.
    /// </summary>
    [Fact]
    public async Task Discovery_MatchingTwoCis_IsQueuedAsAmbiguousAndNamesBothContenders()
    {
        var address = NewAddress();
        var firstCi = await CreateNetworkCiAsync(NewName("twin-a"), address);
        var secondCi = await CreateNetworkCiAsync(NewName("twin-b"), address);

        var result = await IngestAsync(Discovery(address));

        Assert.Null(result.CiId);
        Assert.Equal(DiscoveryMatchRule.Ambiguous, result.Rule);

        var card = await GetAsync<DiscoveredDeviceDto>($"/api/discovered-devices/{result.DiscoveredDeviceId}");
        Assert.Equal("Pending", card.Status);
        Assert.Equal("Ambiguous", card.MatchRule);
        Assert.Equal([firstCi, secondCi], card.Contenders.Select(item => item.CiId).Order().ToArray());
    }

    /// <summary>Settling an ambiguity: the approver names the CI, and no second CI is created.</summary>
    [Fact]
    public async Task Approve_OntoAnExistingCi_AttachesTheDiscoveryWithoutCreatingAnything()
    {
        var address = NewAddress();
        var ciId = await CreateNetworkCiAsync(NewName("chosen"), "10.98.0.1");
        var ingest = await IngestAsync(Discovery(address, sysName: NewName("unplaced")));

        var approved = await PostAsync<DiscoveredDeviceDto>(
            $"/api/discovered-devices/{ingest.DiscoveredDeviceId}/approvals",
            new { ciId, note = "Same box, different interface." });

        Assert.Equal("Approved", approved.Status);
        Assert.Equal(ciId, approved.CiId);

        var facts = await GetAsync<CiDiscoveryFactsDto>($"/api/cis/{ciId}/discovery-facts");
        Assert.Equal(address, facts.Address);
    }

    // ---- Failure paths ------------------------------------------------------------------------

    /// <summary>
    /// The failure path this package's own design forces: a scan observes an address and a name, and
    /// never a vendor or a port count. Approving without them is a 400 that names the fields rather
    /// than a CI filled with "Unknown".
    /// </summary>
    [Fact]
    public async Task Approve_WithoutTheAttributesItsTypeRequires_Is400NamingEachMissingField()
    {
        var ingest = await IngestAsync(Discovery(NewAddress(), sysName: NewName("incomplete")));

        var response = await Client.PostAsJsonAsync(
            $"/api/discovered-devices/{ingest.DiscoveredDeviceId}/approvals",
            new { type = nameof(CiType.NetworkDevice) });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDto>();
        Assert.NotNull(problem);
        Assert.Contains("attributes.vendor", problem.Errors.Keys);
        Assert.Contains("attributes.portCount", problem.Errors.Keys);
        // The one attribute a scan does observe is not among the complaints.
        Assert.DoesNotContain("attributes.managementIp", problem.Errors.Keys);

        // And the card is untouched, so the approver can fix the form and try again.
        var card = await GetAsync<DiscoveredDeviceDto>($"/api/discovered-devices/{ingest.DiscoveredDeviceId}");
        Assert.Equal("Pending", card.Status);
    }

    [Fact]
    public async Task Approve_ADiscoveryAlreadyReviewed_Is409AndCreatesNoSecondCi()
    {
        var ingest = await IngestAsync(Discovery(NewAddress(), sysName: NewName("twice")));
        var body = new
        {
            type = nameof(CiType.NetworkDevice),
            attributes = new Dictionary<string, string> { ["vendor"] = "Cisco", ["portCount"] = "12" },
        };
        var first = await PostAsync<DiscoveredDeviceDto>(
            $"/api/discovered-devices/{ingest.DiscoveredDeviceId}/approvals", body);

        var response = await Client.PostAsJsonAsync(
            $"/api/discovered-devices/{ingest.DiscoveredDeviceId}/approvals", body);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var card = await GetAsync<DiscoveredDeviceDto>($"/api/discovered-devices/{ingest.DiscoveredDeviceId}");
        Assert.Equal(first.CiId, card.CiId);
    }

    [Fact]
    public async Task Reject_ADiscoveryAlreadyApproved_Is409()
    {
        var ingest = await IngestAsync(Discovery(NewAddress(), sysName: NewName("settled")));
        await PostAsync<DiscoveredDeviceDto>(
            $"/api/discovered-devices/{ingest.DiscoveredDeviceId}/approvals",
            new
            {
                type = nameof(CiType.NetworkDevice),
                attributes = new Dictionary<string, string> { ["vendor"] = "Cisco", ["portCount"] = "12" },
            });

        var response = await Client.PostAsJsonAsync(
            $"/api/discovered-devices/{ingest.DiscoveredDeviceId}/rejections", new { note = "changed my mind" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Approve_NamingBothAnExistingCiAndAType_Is400()
    {
        var ingest = await IngestAsync(Discovery(NewAddress(), sysName: NewName("confused")));
        var ciId = await CreateNetworkCiAsync(NewName("target"), "10.97.0.1");

        var response = await Client.PostAsJsonAsync(
            $"/api/discovered-devices/{ingest.DiscoveredDeviceId}/approvals",
            new { ciId, type = nameof(CiType.NetworkDevice) });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Approve_ADiscoveryThatDoesNotExist_Is404()
    {
        var response = await Client.PostAsJsonAsync(
            $"/api/discovered-devices/{Guid.CreateVersion7()}/approvals",
            new { type = nameof(CiType.Hardware) });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DiscoveryFacts_ForACiNoScanHasEverSeen_Is404()
    {
        var ciId = await CreateNetworkCiAsync(NewName("unseen"), "10.96.0.1");

        var response = await Client.GetAsync($"/api/cis/{ciId}/discovery-facts");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task List_AnUnknownStatus_Is400RatherThanASilentlyEmptyQueue()
    {
        var response = await Client.GetAsync("/api/discovered-devices?status=Maybe");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// The review queue is an agent surface, and the scanner's own role must not reach it: a scanner
    /// that could approve its own findings would make the queue decorative. WP-4.1 made `CanDiscover`
    /// disjoint from every operator policy; this is the other side of that.
    /// </summary>
    [Fact]
    public async Task ReviewQueue_WithTheScannersOwnRole_IsForbidden()
    {
        foreach (var role in new[] { "Discovery", "Poller", "EndUser" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/discovered-devices");
            request.Headers.Add(DiscoveryReviewAuthenticationHandler.RoleHeader, role);
            using var response = await Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    // ---- Helpers ------------------------------------------------------------------------------

    private HttpClient Client => _client ?? throw new InvalidOperationException("Not initialised.");

    private async Task<DiscoveryIntakeResult> IngestAsync(DeviceDiscovered discovery)
    {
        await using var scope = _application.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IDiscoveryReviewService>()
            .IngestAsync(discovery, CancellationToken.None);
    }

    private async Task EnrollAsync(DiscoveredDeviceApproved approval)
    {
        await using var scope = _application.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IDiscoveryEnrollmentService>()
            .EnrollAsync(approval, CancellationToken.None);
    }

    private async Task<Guid> CreateNetworkCiAsync(
        string name,
        string managementIp,
        string vendor = "Cisco",
        int portCount = 24)
    {
        var created = await PostAsync<CiDto>("/api/cis", new
        {
            type = nameof(CiType.NetworkDevice),
            name,
            attributes = new Dictionary<string, string>
            {
                ["managementIp"] = managementIp,
                ["vendor"] = vendor,
                ["portCount"] = portCount.ToString(),
            },
        });
        return created.Id;
    }

    private async Task CreateMonitoredDeviceAsync(Guid ciId, string address)
    {
        await using var scope = _application.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IMonitoredDeviceService>();
        var actor = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "test"), new Claim(ClaimTypes.Name, "test")], "Test"));
        var result = await service.CreateAsync(
            new CreateMonitoredDeviceRequest(ciId, address), actor, CancellationToken.None);
        Assert.Equal(MonitoringOutcome.Success, result.Outcome);
    }

    private async Task<T> GetAsync<T>(string url)
    {
        using var response = await Client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<T> PostAsync<T>(string url, object body)
    {
        using var response = await Client.PostAsJsonAsync(url, body);
        Assert.True(
            response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created,
            $"POST {url} answered {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    /// <summary>
    /// WP-4.1's committed envelope, read with MassTransit's own serializer options — the same bytes
    /// <c>DiscoveryEnvelopeTests</c> asserts and <c>test_bus.py</c> writes.
    /// </summary>
    private static DeviceDiscovered FixtureDiscovery()
    {
        var path = Path.Combine(
            RepositoryRoot(), "services", "discovery", "tests", "fixtures", "discovered-envelope.json");
        using var envelope = JsonDocument.Parse(File.ReadAllText(path));
        var message = envelope.RootElement.GetProperty("message").GetRawText();
        return JsonSerializer.Deserialize<DeviceDiscovered>(message, SystemTextJsonMessageSerializer.Options)
            ?? throw new InvalidOperationException("The discovery envelope fixture did not deserialise.");
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "services")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate the repository root from the test output directory.");
    }

    private static DeviceDiscovered Discovery(
        string address,
        string? sysName = null,
        string? sysDescription = null,
        IReadOnlyList<DiscoveredNeighbour>? neighbours = null,
        string? hostname = null,
        string? hostnameSource = null) => new(
        Guid.CreateVersion7(),
        DateTimeOffset.UtcNow,
        "discovery-tests",
        Guid.CreateVersion7(),
        "Integration sweep",
        Guid.CreateVersion7(),
        address,
        hostname,
        hostnameSource,
        RespondedToPing: true,
        OpenPorts: [22],
        Snmp: sysName is null && sysDescription is null
            ? null
            : new DiscoveredSnmpIdentity(sysName, sysDescription, "1.3.6.1.4.1.9.1.1", null, null, 3_600),
        Neighbours: neighbours ?? []);

    /// <summary>
    /// Addresses come from TEST-NET-2, one per test, because every test in this class shares one
    /// database and the ledger is keyed on identity — two tests reusing an address would each see the
    /// other's card. The same shared-table trap WP-3.10 and WP-3.12 both recorded, in a new place.
    /// </summary>
    private static string NewAddress()
    {
        var suffix = Interlocked.Increment(ref _addressCounter);
        return $"198.51.{suffix / 250 % 250}.{suffix % 250}";
    }

    /// <summary>
    /// A short random suffix from <c>Guid.NewGuid</c> and never from <c>CreateVersion7</c>: a v7 GUID
    /// opens with a millisecond timestamp, so its first eight characters are identical for every GUID
    /// made in the same minute — which WP-3.10 recorded as costing two rounds of debugging.
    /// </summary>
    private static string NewName(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..24];

    private static int _addressCounter = Random.Shared.Next(1, 40_000);

    // ---- Wire shapes --------------------------------------------------------------------------

    private sealed record DiscoveredDeviceDto(
        Guid Id,
        string IdentityKey,
        string Address,
        string? Hostname,
        string? HostnameSource,
        bool RespondedToPing,
        IReadOnlyList<int> OpenPorts,
        SnmpDto? Snmp,
        IReadOnlyList<NeighbourDto> Neighbours,
        string Status,
        Guid? CiId,
        string? CiName,
        string MatchRule,
        IReadOnlyList<ContenderDto> Contenders,
        string SuggestedType,
        string SuggestedName,
        IReadOnlyDictionary<string, string> SuggestedAttributes,
        int SightingCount,
        string? ReviewedBy,
        string? ReviewNote);

    private sealed record SnmpDto(
        string? SysName,
        string? SysDescription,
        string? SysObjectId,
        string? SysLocation,
        string? SysContact,
        double? UptimeSeconds);

    private sealed record NeighbourDto(
        string Protocol,
        string? LocalPort,
        string? RemoteSystemName,
        string? RemotePort,
        string? RemoteAddress);

    private sealed record ContenderDto(Guid CiId, string Name, string Type);

    private sealed record DiscoveredDevicePageDto(
        IReadOnlyList<DiscoveredDeviceDto> Items,
        int Total,
        int Page,
        int PageSize);

    private sealed record CiDiscoveryFactsDto(
        Guid CiId,
        string Address,
        string? Hostname,
        bool RespondedToPing,
        IReadOnlyList<int> OpenPorts,
        SnmpDto? Snmp,
        IReadOnlyList<NeighbourDto> Neighbours,
        string DiscoveryName,
        string ScanProfileName,
        int SightingCount);

    private sealed record CiDto(Guid Id, string Name, IReadOnlyDictionary<string, string> Attributes);

    private sealed record ValidationProblemDto(IReadOnlyDictionary<string, string[]> Errors);

    // ---- Host ---------------------------------------------------------------------------------

    private sealed class DiscoveryReviewApplication : WebApplicationFactory<Program>
    {
        private readonly string _connectionString;
        private readonly string _rabbitMqConnectionString;
        private readonly string _minioConnectionString;

        public DiscoveryReviewApplication(
            string connectionString,
            string rabbitMqConnectionString,
            string minioConnectionString)
        {
            _connectionString = connectionString;
            _rabbitMqConnectionString = rabbitMqConnectionString;
            _minioConnectionString = minioConnectionString;
            Environment.SetEnvironmentVariable("ConnectionStrings__database", connectionString);
            Environment.SetEnvironmentVariable("ConnectionStrings__rabbitmq", rabbitMqConnectionString);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Authentication:Authority"] = "https://identity.example.test/realms/it-platform",
                    ["Authentication:Audience"] = "it-platform-api",
                    ["Authentication:ClientId"] = "it-platform-web",
                    ["Authentication:PostLogoutRedirectUri"] = "https://app.example.test/",
                    ["ConnectionStrings:database"] = _connectionString,
                    ["ConnectionStrings:rabbitmq"] = _rabbitMqConnectionString,
                    ["ConnectionStrings:minio"] = _minioConnectionString,
                    ["ObjectStorage:AccessKey"] = "minioadmin",
                    ["ObjectStorage:SecretKey"] = "minio-test-password",
                    ["Platform:ApplyMigrations"] = "false",
                    ["Platform:EnableMessageBus"] = "false",
                    ["Platform:EnableScheduler"] = "false",
                }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.AddAuthentication(options =>
                    {
                        options.DefaultAuthenticateScheme = DiscoveryReviewAuthenticationHandler.TestScheme;
                        options.DefaultChallengeScheme = DiscoveryReviewAuthenticationHandler.TestScheme;
                        options.DefaultForbidScheme = DiscoveryReviewAuthenticationHandler.TestScheme;
                    })
                    .AddScheme<AuthenticationSchemeOptions, DiscoveryReviewAuthenticationHandler>(
                        DiscoveryReviewAuthenticationHandler.TestScheme,
                        _ => { });
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            Environment.SetEnvironmentVariable("ConnectionStrings__database", null);
            Environment.SetEnvironmentVariable("ConnectionStrings__rabbitmq", null);
        }
    }

    private sealed class DiscoveryReviewAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string TestScheme = "DiscoveryReviewTest";
        public const string RoleHeader = "X-Test-Role";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(RoleHeader, out var role))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, "discovery-test-user-id"),
                    new Claim(ClaimTypes.Name, "discovery-test-user"),
                    new Claim(ClaimTypes.Role, role.ToString()),
                ],
                TestScheme);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), TestScheme)));
        }
    }
}
