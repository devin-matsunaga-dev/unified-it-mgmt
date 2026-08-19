using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;

using Contracts.Events;

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
using Modules.Assets.Features.Discovery;
using Modules.Helpdesk.Data;
using Modules.Monitoring.Data;
using Platform.Data;

namespace Infrastructure.Tests;

/// <summary>
/// The topology map as the browser reads it: the relationships an operator asserted, the LLDP links a
/// scan observed, and the saved layouts somebody arranged by hand.
/// <para>
/// The neighbour half is driven through the real WP-4.2 intake rather than by writing
/// <c>ci_discovery_facts</c> rows by hand, so what this asserts is that the neighbours WP-4.1 puts on
/// the wire actually reach the map.
/// </para>
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class TopologyApiIntegrationTests : IAsyncLifetime
{
    private readonly TopologyApplication _application;
    private HttpClient? _client;

    private readonly InfrastructureFixture _infrastructure;

    public TopologyApiIntegrationTests(InfrastructureFixture infrastructure)
    {
        _infrastructure = infrastructure;

        // The map is the whole estate, and the estate here is one database shared by the entire suite —
        // so the rendering budget has to be lifted out of the way or this class's own nodes compete for
        // it with every CI every other test has ever created. That is the shared-table trap in a new
        // shape: not a test asserting a table is empty, but a test asserting something about all of it.
        _application = NewApplication(nodeLimit: 100_000);
    }

    private TopologyApplication NewApplication(int nodeLimit) => new(
        _infrastructure.PostgresConnectionString,
        _infrastructure.RabbitMqConnectionString,
        _infrastructure.MinioConnectionString,
        nodeLimit);

    public async Task InitializeAsync()
    {
        _client = _application.CreateClient();
        await using var scope = _application.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<PlatformDbContext>().Database.MigrateAsync();
        await scope.ServiceProvider.GetRequiredService<AssetsDbContext>().Database.MigrateAsync();

        // Both ports a CI touches, migrated for the same reason. The discovery intake's top rung reads
        // Monitoring through IMonitoredAddressDirectory, and deleting a CI asks Helpdesk through
        // ITicketLinkDirectory whether a ticket still points at it — an unmigrated schema behind either
        // is a 500 from a query that mentions neither this feature nor that module. The fifth package to
        // meet this trap, and the first to meet the Helpdesk half of it.
        await scope.ServiceProvider.GetRequiredService<MonitoringDbContext>().Database.MigrateAsync();
        await scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>().Database.MigrateAsync();
    }

    public Task DisposeAsync()
    {
        _application.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>
    /// The WP's first verification step, in miniature: the map matches the topology that is recorded.
    /// </summary>
    [Fact]
    public async Task Topology_ForARelatedEstate_DrawsEveryCiAndEveryEdgeBetweenThem()
    {
        var estate = await BuildEstateAsync();

        var topology = await GetTopologyAsync();

        Assert.Contains(topology.Nodes, node => node.CiId == estate.Switch.Id);
        Assert.Contains(topology.Nodes, node => node.CiId == estate.Router.Id);
        Assert.Contains(topology.Nodes, node => node.CiId == estate.Host.Id);

        var uplink = Assert.Single(topology.Edges, edge =>
            edge.SourceCiId == estate.Switch.Id && edge.TargetCiId == estate.Router.Id);
        Assert.Equal("ConnectsTo", uplink.Type);
        Assert.Contains(topology.Edges, edge =>
            edge.SourceCiId == estate.Host.Id && edge.TargetCiId == estate.Switch.Id);

        // A network CI's node carries the address an operator would type, so two identically shaped
        // nodes can be told apart without opening either.
        var switchNode = Assert.Single(topology.Nodes, node => node.CiId == estate.Switch.Id);
        Assert.Equal(estate.SwitchAddress, switchNode.Address);
        Assert.False(topology.NodeLimitReached);
    }

    /// <summary>
    /// A truncated picture must never look like a complete one — the flag WP-2.4 established for the
    /// CI page's mini-graph, applied to the estate-wide map. The cut is by edge count, so what survives
    /// is the connected core rather than an arbitrary slice.
    /// </summary>
    [Fact]
    public async Task Topology_WithMoreCisThanTheRenderingBudget_TruncatesByDegreeAndSaysSo()
    {
        await BuildEstateAsync();

        using var budgeted = NewApplication(nodeLimit: 3);
        using var client = budgeted.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/topology");
        request.Headers.Add(TopologyAuthenticationHandler.RoleHeader, "Technician");
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var topology = Assert.IsType<TopologyDto>(await response.Content.ReadFromJsonAsync<TopologyDto>());
        Assert.True(topology.NodeLimitReached);
        Assert.Equal(3, topology.NodeLimit);
        Assert.Equal(3, topology.Nodes.Count);

        // No edge is left dangling: an edge whose other end was cut goes with it.
        var drawn = topology.Nodes.Select(node => node.CiId).ToHashSet();
        Assert.All(topology.Edges, edge =>
            Assert.True(drawn.Contains(edge.SourceCiId) && drawn.Contains(edge.TargetCiId)));
    }

    /// <summary>
    /// The package's whole point: a scan's LLDP report becomes a link on the map, and it does that
    /// without anything being written to <c>assets.ci_relationships</c>.
    /// </summary>
    [Fact]
    public async Task Topology_AfterAScanReportsANeighbour_DrawsAnObservedLinkAndWritesNoRelationship()
    {
        var estate = await BuildEstateAsync();

        // A CI that exists but that nothing joins to the switch — the cable somebody patched and
        // never recorded, which is the case this whole feature is worth having for.
        var unrecorded = $"sw-unrecorded-{Guid.NewGuid():N}"[..24];
        await CreateCiAsync("NetworkDevice", unrecorded, new()
        {
            ["managementIp"] = $"10.96.{Random.Shared.Next(1, 250)}.{Random.Shared.Next(1, 250)}",
            ["vendor"] = "Cisco",
            ["portCount"] = "24",
        });
        var relationshipsBefore = await CountRelationshipsAsync();

        await IngestAsync(estate.SwitchAddress, estate.SwitchSysName,
        [
            new DiscoveredNeighbour("lldp", "GigabitEthernet0/1", estate.RouterSysName, "GigabitEthernet0/24", null),
            new DiscoveredNeighbour("lldp", "GigabitEthernet0/2", unrecorded, "GigabitEthernet0/1", null),
        ]);

        var topology = await GetTopologyAsync();
        var unrecordedId = Assert.Single(topology.Nodes, node => node.Name == unrecorded).CiId;

        var toRouter = Assert.Single(topology.ObservedLinks, link =>
            Ends(link).SetEquals([estate.Switch.Id, estate.Router.Id]));
        Assert.True(toRouter.MatchesAssertedEdge);
        Assert.Equal(["lldp"], toRouter.Protocols);
        Assert.False(toRouter.ConfirmedByBothEnds);

        var toUnrecorded = Assert.Single(topology.ObservedLinks, link =>
            Ends(link).SetEquals([estate.Switch.Id, unrecordedId]));
        Assert.False(toUnrecorded.MatchesAssertedEdge);

        // The asserted edge the scan agreed with says so, and the one it said nothing about does not.
        Assert.True(Assert.Single(topology.Edges, edge =>
            edge.SourceCiId == estate.Switch.Id && edge.TargetCiId == estate.Router.Id).ObservedByDiscovery);
        Assert.False(Assert.Single(topology.Edges, edge =>
            edge.SourceCiId == estate.Host.Id && edge.TargetCiId == estate.Switch.Id).ObservedByDiscovery);

        // A scan observes; an operator asserts. The link to the unrecorded switch is exactly the edge
        // it would be tempting to write, and writing it would destroy the difference WP-4.6's drift
        // report exists to find.
        Assert.Equal(relationshipsBefore, await CountRelationshipsAsync());
    }

    /// <summary>Two devices reporting one cable are one link on the map, not two arrows.</summary>
    [Fact]
    public async Task Topology_WhenBothEndsReportTheSameCable_ShowsOneLinkConfirmedFromBothSides()
    {
        var estate = await BuildEstateAsync();

        await IngestAsync(estate.SwitchAddress, estate.SwitchSysName,
            [new DiscoveredNeighbour("lldp", "GigabitEthernet0/1", estate.RouterSysName, "GigabitEthernet0/24", null)]);
        await IngestAsync(estate.RouterAddress, estate.RouterSysName,
            [new DiscoveredNeighbour("lldp", "GigabitEthernet0/24", estate.SwitchSysName, "GigabitEthernet0/1", null)]);

        var topology = await GetTopologyAsync();

        var link = Assert.Single(topology.ObservedLinks, item =>
            Ends(item).SetEquals([estate.Switch.Id, estate.Router.Id]));
        Assert.True(link.ConfirmedByBothEnds);
        Assert.Equal(["GigabitEthernet0/1", "GigabitEthernet0/24"],
            new[] { link.SourcePort, link.TargetPort }.Order());
    }

    /// <summary>
    /// A neighbour the CMDB knows nothing about is counted and named, never drawn. A node on this map
    /// is a CI; an unknown device becomes one in WP-4.2's review queue or not at all.
    /// </summary>
    [Fact]
    public async Task Topology_WithANeighbourNoCiAnswersTo_ReportsItWithoutInventingANode()
    {
        var estate = await BuildEstateAsync();
        var stranger = $"printer-{Guid.NewGuid():N}"[..20];

        await IngestAsync(estate.SwitchAddress, estate.SwitchSysName,
            [new DiscoveredNeighbour("lldp", "GigabitEthernet0/9", stranger, "eth0", null)]);

        var topology = await GetTopologyAsync();

        var unresolved = Assert.Single(topology.UnresolvedNeighbours, item => item.RemoteSystemName == stranger);
        Assert.Equal(estate.Switch.Id, unresolved.ReportedByCiId);
        Assert.Equal("NoCandidate", unresolved.Reason);
        Assert.Equal("GigabitEthernet0/9", unresolved.LocalPort);
        Assert.DoesNotContain(topology.Nodes, node => node.Name == stranger);
        Assert.DoesNotContain(topology.ObservedLinks, link =>
            Ends(link).Contains(estate.Switch.Id) && Ends(link).Count == 2 && !Ends(link).Contains(estate.Router.Id)
            && !Ends(link).Contains(estate.Host.Id));
    }

    /// <summary>A scan that has answered gives the node the one fact on it that the CMDB did not.</summary>
    [Fact]
    public async Task Topology_ForACiAScanHasSeen_CarriesItsLastSeenAndOnlyForThatCi()
    {
        var estate = await BuildEstateAsync();
        await IngestAsync(estate.SwitchAddress, estate.SwitchSysName, []);

        var topology = await GetTopologyAsync();

        Assert.NotNull(Assert.Single(topology.Nodes, node => node.CiId == estate.Switch.Id)
            .LastSeenByDiscoveryAt);
        Assert.Null(Assert.Single(topology.Nodes, node => node.CiId == estate.Host.Id)
            .LastSeenByDiscoveryAt);
    }

    [Fact]
    public async Task Topology_FilteredToNetworkDevices_DropsTheOtherNodesAndTheEdgesIntoThem()
    {
        var estate = await BuildEstateAsync();

        var topology = await GetTopologyAsync("?types=NetworkDevice");

        Assert.Contains(topology.Nodes, node => node.CiId == estate.Switch.Id);
        Assert.DoesNotContain(topology.Nodes, node => node.CiId == estate.Host.Id);

        // An edge with one end filtered off the map has nowhere to land.
        Assert.DoesNotContain(topology.Edges, edge =>
            edge.SourceCiId == estate.Host.Id || edge.TargetCiId == estate.Host.Id);
        Assert.Contains(topology.Edges, edge =>
            edge.SourceCiId == estate.Switch.Id && edge.TargetCiId == estate.Router.Id);
    }

    /// <summary>Failure path: a filter naming something that is not a CI type is a 400, not an empty map.</summary>
    [Fact]
    public async Task Topology_WithATypeThatDoesNotExist_ReturnsValidationProblem()
    {
        using var request = Authenticated(HttpMethod.Get, "/api/topology?types=NetworkDevice,Toaster");
        using var response = await _client!.SendAsync(request);
        var problem = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("is not a CI type", problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Topology_AsEndUser_IsForbidden()
    {
        using var request = Authenticated(HttpMethod.Get, "/api/topology", "EndUser");
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>The WP's third verification step: a saved manual layout persists.</summary>
    [Fact]
    public async Task TopologyMap_SavedThenReadBack_KeepsEveryPinnedPosition()
    {
        var estate = await BuildEstateAsync();
        var name = $"Core network {Guid.NewGuid():N}";

        var created = await SaveAsync(HttpMethod.Post, "/api/topology-maps", new
        {
            name,
            description = "Hand-arranged for the NOC wall.",
            nodes = new[]
            {
                new { ciId = estate.Switch.Id, x = 120.5, y = -40.25 },
                new { ciId = estate.Router.Id, x = 480.0, y = 0.0 },
            },
        });

        var read = await GetAsync<TopologyMapDto>($"/api/topology-maps/{created.Id}");
        Assert.Equal(name, read.Name);
        Assert.Equal(2, read.Nodes.Count);
        var pinnedSwitch = Assert.Single(read.Nodes, node => node.CiId == estate.Switch.Id);
        Assert.Equal(120.5, pinnedSwitch.X);
        Assert.Equal(-40.25, pinnedSwitch.Y);

        var summaries = await GetAsync<List<TopologyMapSummaryDto>>("/api/topology-maps");
        var summary = Assert.Single(summaries, item => item.Id == created.Id);
        Assert.Equal(2, summary.PinnedNodeCount);
        Assert.Equal("topology-test-user-id", summary.CreatedBy);
    }

    /// <summary>
    /// A save states where everything on the canvas now sits, so a node that was un-pinned has to be
    /// able to disappear. Merging instead would make un-pinning impossible.
    /// </summary>
    [Fact]
    public async Task TopologyMap_SavedAgainWithFewerPins_ReplacesThePositionsRatherThanMergingThem()
    {
        var estate = await BuildEstateAsync();
        var created = await SaveAsync(HttpMethod.Post, "/api/topology-maps", new
        {
            name = $"Rearranged {Guid.NewGuid():N}",
            nodes = new[]
            {
                new { ciId = estate.Switch.Id, x = 10.0, y = 10.0 },
                new { ciId = estate.Router.Id, x = 20.0, y = 20.0 },
            },
        });

        var updated = await SaveAsync(HttpMethod.Put, $"/api/topology-maps/{created.Id}", new
        {
            name = created.Name,
            nodes = new[] { new { ciId = estate.Switch.Id, x = 99.0, y = 99.0 } },
        });

        var node = Assert.Single(updated.Nodes);
        Assert.Equal(estate.Switch.Id, node.CiId);
        Assert.Equal(99.0, node.X);
        Assert.Equal("topology-test-user-id", updated.UpdatedBy);
    }

    /// <summary>
    /// A map is a set of pins rather than a snapshot, so a CI racked after the map was saved appears
    /// on it — unpinned, and therefore auto-laid-out — instead of being invisible until somebody
    /// redraws the picture.
    /// </summary>
    [Fact]
    public async Task TopologyMap_DoesNotFreezeTheEstateItWasSavedAgainst()
    {
        var estate = await BuildEstateAsync();
        var created = await SaveAsync(HttpMethod.Post, "/api/topology-maps", new
        {
            name = $"Before the new switch {Guid.NewGuid():N}",
            nodes = new[] { new { ciId = estate.Switch.Id, x = 10.0, y = 10.0 } },
        });

        var newcomer = await CreateCiAsync("NetworkDevice", $"Switch racked later {Guid.NewGuid():N}");
        using var relate = Authenticated(HttpMethod.Post, $"/api/cis/{newcomer.Id}/relationships");
        relate.Content = JsonContent.Create(new { targetCiId = estate.Router.Id, type = "ConnectsTo" });
        using var related = await _client!.SendAsync(relate);
        Assert.Equal(HttpStatusCode.Created, related.StatusCode);

        var topology = await GetTopologyAsync();
        var map = await GetAsync<TopologyMapDto>($"/api/topology-maps/{created.Id}");

        Assert.Contains(topology.Nodes, node => node.CiId == newcomer.Id);
        Assert.DoesNotContain(map.Nodes, node => node.CiId == newcomer.Id);
    }

    /// <summary>Failure path: pinning a CI that does not exist names it rather than failing on a foreign key.</summary>
    [Fact]
    public async Task TopologyMap_PinningACiThatDoesNotExist_ReturnsValidationProblemNamingIt()
    {
        var missing = Guid.CreateVersion7();
        using var request = Authenticated(HttpMethod.Post, "/api/topology-maps");
        request.Content = JsonContent.Create(new
        {
            name = $"Map of a ghost {Guid.NewGuid():N}",
            nodes = new[] { new { ciId = missing, x = 1.0, y = 1.0 } },
        });
        using var response = await _client!.SendAsync(request);
        var problem = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains(missing.ToString(), problem, StringComparison.Ordinal);
        Assert.Contains("does not exist", problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TopologyMap_WithANameAnotherMapHas_ReturnsConflict()
    {
        var name = $"Duplicated {Guid.NewGuid():N}";
        await SaveAsync(HttpMethod.Post, "/api/topology-maps", new { name, nodes = Array.Empty<object>() });

        using var request = Authenticated(HttpMethod.Post, "/api/topology-maps");
        request.Content = JsonContent.Create(new { name = name.ToUpperInvariant(), nodes = Array.Empty<object>() });
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("already exists", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Failure path: a coordinate that is not a finite number would render the canvas blank with
    /// nothing to say why, and it can only arrive from a broken client.
    /// </summary>
    [Fact]
    public async Task TopologyMap_WithANonFinitePosition_ReturnsValidationProblem()
    {
        var estate = await BuildEstateAsync();
        using var request = Authenticated(HttpMethod.Post, "/api/topology-maps");
        request.Content = new StringContent(
            $$"""
            {"name":"Broken canvas {{Guid.NewGuid():N}}",
             "nodes":[{"ciId":"{{estate.Switch.Id}}","x":"NaN","y":0}]}
            """,
            System.Text.Encoding.UTF8,
            "application/json");
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task TopologyMap_Deleted_IsGoneAndIsAudited()
    {
        var estate = await BuildEstateAsync();
        var created = await SaveAsync(HttpMethod.Post, "/api/topology-maps", new
        {
            name = $"Temporary {Guid.NewGuid():N}",
            nodes = new[] { new { ciId = estate.Switch.Id, x = 5.0, y = 5.0 } },
        });

        using var delete = Authenticated(HttpMethod.Delete, $"/api/topology-maps/{created.Id}");
        using var deleted = await _client!.SendAsync(delete);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        using var reread = Authenticated(HttpMethod.Get, $"/api/topology-maps/{created.Id}");
        using var missing = await _client!.SendAsync(reread);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        await using var scope = _application.Services.CreateAsyncScope();
        var platform = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var entityId = created.Id.ToString();
        var entries = await platform.AuditEntries
            .Where(entry => entry.EntityType == "TopologyMap" && entry.EntityId == entityId)
            .ToListAsync();
        Assert.Contains(entries, entry => entry.Action == "Created");
        Assert.Contains(entries, entry => entry.Action == "Deleted");
    }

    [Fact]
    public async Task TopologyMap_ThatDoesNotExist_IsNotFoundOnEveryVerb()
    {
        var missing = Guid.CreateVersion7();

        using var read = Authenticated(HttpMethod.Get, $"/api/topology-maps/{missing}");
        using var readResponse = await _client!.SendAsync(read);
        Assert.Equal(HttpStatusCode.NotFound, readResponse.StatusCode);

        using var save = Authenticated(HttpMethod.Put, $"/api/topology-maps/{missing}");
        save.Content = JsonContent.Create(new { name = "Nowhere", nodes = Array.Empty<object>() });
        using var saveResponse = await _client!.SendAsync(save);
        Assert.Equal(HttpStatusCode.NotFound, saveResponse.StatusCode);

        using var delete = Authenticated(HttpMethod.Delete, $"/api/topology-maps/{missing}");
        using var deleteResponse = await _client!.SendAsync(delete);
        Assert.Equal(HttpStatusCode.NotFound, deleteResponse.StatusCode);
    }

    /// <summary>A pin is a position for a CI, so a CI leaving the estate takes its pins with it.</summary>
    [Fact]
    public async Task TopologyMap_WhenAPinnedCiIsDeleted_LosesThatPinAndKeepsTheRest()
    {
        var estate = await BuildEstateAsync();
        var doomed = await CreateCiAsync("NetworkDevice", $"Switch about to be scrapped {Guid.NewGuid():N}");
        var created = await SaveAsync(HttpMethod.Post, "/api/topology-maps", new
        {
            name = $"Outliving its nodes {Guid.NewGuid():N}",
            nodes = new[]
            {
                new { ciId = estate.Switch.Id, x = 1.0, y = 1.0 },
                new { ciId = doomed.Id, x = 2.0, y = 2.0 },
            },
        });

        using var delete = Authenticated(HttpMethod.Delete, $"/api/cis/{doomed.Id}");
        using var deleted = await _client!.SendAsync(delete);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        var map = await GetAsync<TopologyMapDto>($"/api/topology-maps/{created.Id}");
        Assert.Equal(estate.Switch.Id, Assert.Single(map.Nodes).CiId);
    }

    [Fact]
    public async Task TopologyMaps_AsEndUser_AreForbidden()
    {
        using var request = Authenticated(HttpMethod.Get, "/api/topology-maps", "EndUser");
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>A switch uplinked to a router, with a host hanging off the switch.</summary>
    private async Task<Estate> BuildEstateAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var switchAddress = $"10.99.{Random.Shared.Next(1, 250)}.{Random.Shared.Next(1, 250)}";
        var routerAddress = $"10.98.{Random.Shared.Next(1, 250)}.{Random.Shared.Next(1, 250)}";
        var switchSysName = $"sw-{suffix}";
        var routerSysName = $"rtr-{suffix}";
        var hostHostname = $"esx-{suffix}";

        var switchCi = await CreateCiAsync("NetworkDevice", switchSysName, new()
        {
            ["managementIp"] = switchAddress,
            ["vendor"] = "Cisco",
            ["portCount"] = "48",
        });
        var routerCi = await CreateCiAsync("NetworkDevice", routerSysName, new()
        {
            ["managementIp"] = routerAddress,
            ["vendor"] = "Cisco",
            ["portCount"] = "24",
        });
        var host = await CreateCiAsync("Server", $"Host {suffix}", new()
        {
            ["hostname"] = hostHostname,
            ["operatingSystem"] = "Ubuntu 24.04",
            ["cpuCores"] = "16",
            ["ramGb"] = "128",
        });

        foreach (var (source, target) in ((Guid, Guid)[])
                 [(switchCi.Id, routerCi.Id), (host.Id, switchCi.Id)])
        {
            using var request = Authenticated(HttpMethod.Post, $"/api/cis/{source}/relationships");
            request.Content = JsonContent.Create(new { targetCiId = target, type = "ConnectsTo" });
            using var response = await _client!.SendAsync(request);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        return new Estate(
            switchCi, routerCi, host, switchAddress, routerAddress, switchSysName, routerSysName, hostHostname);
    }

    /// <summary>
    /// One scan sighting, through the real intake. The address is a management IP a CI records, so the
    /// discovery matches that CI and its neighbours land in <c>ci_discovery_facts</c> — which is the
    /// only door those neighbours have into this map.
    /// </summary>
    private async Task IngestAsync(string address, string sysName, DiscoveredNeighbour[] neighbours)
    {
        await using var scope = _application.Services.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<IDiscoveryReviewService>()
            .IngestAsync(
                new DeviceDiscovered(
                    Guid.CreateVersion7(),
                    DateTimeOffset.UtcNow,
                    "topology-test-scanner",
                    Guid.CreateVersion7(),
                    "Topology test profile",
                    Guid.CreateVersion7(),
                    address,
                    null,
                    null,
                    RespondedToPing: true,
                    [],
                    new DiscoveredSnmpIdentity(sysName, "Simulated switch", "1.3.6.1.4.1.8072.3.2.10", null, null, 1),
                    neighbours),
                CancellationToken.None);

        Assert.Equal(DiscoveredDeviceStatus.Matched, result.Status);
    }

    private async Task<int> CountRelationshipsAsync()
    {
        await using var scope = _application.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AssetsDbContext>()
            .CiRelationships.CountAsync();
    }

    private static HashSet<Guid> Ends(ObservedLinkDto link) => [link.SourceCiId, link.TargetCiId];

    private Task<TopologyDto> GetTopologyAsync(string query = "") =>
        GetAsync<TopologyDto>($"/api/topology{query}");

    private async Task<TopologyMapDto> SaveAsync(HttpMethod method, string uri, object body)
    {
        using var request = Authenticated(method, uri);
        request.Content = JsonContent.Create(body);
        using var response = await _client!.SendAsync(request);
        Assert.Contains(response.StatusCode, (HttpStatusCode[])[HttpStatusCode.Created, HttpStatusCode.OK]);
        return Assert.IsType<TopologyMapDto>(await response.Content.ReadFromJsonAsync<TopologyMapDto>());
    }

    /// <summary>
    /// The name is used verbatim — callers pass one that is already unique. The map's weakest matching
    /// rung compares a neighbour report's remote name against a CI's whole name, so a helper that
    /// quietly appended a uniquifier would make that rung untestable through the API.
    /// </summary>
    private async Task<CiDto> CreateCiAsync(string type, string name, Dictionary<string, string>? attributes = null)
    {
        using var request = Authenticated(HttpMethod.Post, "/api/cis");
        request.Content = JsonContent.Create(new
        {
            type,
            name,
            attributes = attributes ?? new Dictionary<string, string>
            {
                ["managementIp"] = $"10.97.{Random.Shared.Next(1, 250)}.{Random.Shared.Next(1, 250)}",
                ["vendor"] = "Cisco",
                ["portCount"] = "48",
            },
        });
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return Assert.IsType<CiDto>(await response.Content.ReadFromJsonAsync<CiDto>());
    }

    private async Task<T> GetAsync<T>(string uri)
    {
        using var request = Authenticated(HttpMethod.Get, uri);
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Assert.IsType<T>(await response.Content.ReadFromJsonAsync<T>());
    }

    private static HttpRequestMessage Authenticated(HttpMethod method, string uri, string role = "Technician")
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Add(TopologyAuthenticationHandler.RoleHeader, role);
        return request;
    }

    private sealed record Estate(
        CiDto Switch,
        CiDto Router,
        CiDto Host,
        string SwitchAddress,
        string RouterAddress,
        string SwitchSysName,
        string RouterSysName,
        string HostHostname);

    private sealed record CiDto(Guid Id, string Type, string Name);

    private sealed record NodeDto(
        Guid CiId,
        string Name,
        string Type,
        string LifecycleState,
        bool IsActive,
        string? SiteName,
        string? Address,
        DateTimeOffset? LastSeenByDiscoveryAt);

    private sealed record EdgeDto(
        Guid Id,
        Guid SourceCiId,
        Guid TargetCiId,
        string Type,
        string? Description,
        bool ObservedByDiscovery);

    private sealed record ObservedLinkDto(
        string Id,
        Guid SourceCiId,
        Guid TargetCiId,
        List<string> Protocols,
        string? SourcePort,
        string? TargetPort,
        bool ConfirmedByBothEnds,
        bool MatchesAssertedEdge);

    private sealed record UnresolvedDto(
        Guid ReportedByCiId,
        string ReportedByCiName,
        string Protocol,
        string? LocalPort,
        string? RemoteSystemName,
        string? RemotePort,
        string? RemoteAddress,
        string Reason);

    private sealed record TopologyDto(
        List<NodeDto> Nodes,
        List<EdgeDto> Edges,
        List<ObservedLinkDto> ObservedLinks,
        List<UnresolvedDto> UnresolvedNeighbours,
        int NodeLimit,
        bool NodeLimitReached);

    private sealed record TopologyMapNodeDto(Guid CiId, double X, double Y);

    private sealed record TopologyMapDto(
        Guid Id,
        string Name,
        string? Description,
        List<TopologyMapNodeDto> Nodes,
        string CreatedBy,
        DateTimeOffset CreatedAt,
        string? UpdatedBy,
        DateTimeOffset UpdatedAt);

    private sealed record TopologyMapSummaryDto(
        Guid Id,
        string Name,
        string? Description,
        int PinnedNodeCount,
        string CreatedBy,
        DateTimeOffset CreatedAt,
        string? UpdatedBy,
        DateTimeOffset UpdatedAt);

    private sealed class TopologyApplication : WebApplicationFactory<Program>
    {
        private readonly string _connectionString;
        private readonly string _rabbitMqConnectionString;
        private readonly string _minioConnectionString;

        private readonly int _nodeLimit;

        public TopologyApplication(
            string connectionString,
            string rabbitMqConnectionString,
            string minioConnectionString,
            int nodeLimit)
        {
            _connectionString = connectionString;
            _rabbitMqConnectionString = rabbitMqConnectionString;
            _minioConnectionString = minioConnectionString;
            _nodeLimit = nodeLimit;
            Environment.SetEnvironmentVariable("ConnectionStrings__database", connectionString);
            Environment.SetEnvironmentVariable("ConnectionStrings__rabbitmq", rabbitMqConnectionString);
            Environment.SetEnvironmentVariable("Platform__EnableMessageBus", "true");
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
                    // Creating and deleting a CI publishes through the outbox, so the bus has to be
                    // configured even though nothing here reads a message. Every hosted service is
                    // removed below, so no sweeper of this host's competes with another suite's —
                    // WP-3.12's trap avoided rather than re-learned.
                    ["Platform:EnableMessageBus"] = "true",
                    ["Platform:EnableScheduler"] = "false",
                    ["Assets:Topology:NodeLimit"] = _nodeLimit.ToString(System.Globalization.CultureInfo.InvariantCulture),
                }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.AddAuthentication(options =>
                    {
                        options.DefaultAuthenticateScheme = TopologyAuthenticationHandler.TestScheme;
                        options.DefaultChallengeScheme = TopologyAuthenticationHandler.TestScheme;
                        options.DefaultForbidScheme = TopologyAuthenticationHandler.TestScheme;
                    })
                    .AddScheme<AuthenticationSchemeOptions, TopologyAuthenticationHandler>(
                        TopologyAuthenticationHandler.TestScheme,
                        _ => { });
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            Environment.SetEnvironmentVariable("ConnectionStrings__database", null);
            Environment.SetEnvironmentVariable("ConnectionStrings__rabbitmq", null);
            Environment.SetEnvironmentVariable("Platform__EnableMessageBus", null);
        }
    }

    private sealed class TopologyAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string TestScheme = "TopologyTest";
        public const string RoleHeader = "X-Test-Role";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(RoleHeader, out var role))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, "topology-test-user-id"),
                    new Claim(ClaimTypes.Name, "topology-test-user"),
                    new Claim(ClaimTypes.Role, role.ToString()),
                ],
                TestScheme);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), TestScheme)));
        }
    }
}
