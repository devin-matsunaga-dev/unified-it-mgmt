using Modules.Assets.Features.Topology;

namespace Infrastructure.Tests;

/// <summary>
/// The whole of "a pile of one-sided LLDP reports becomes a set of links", with no database anywhere
/// near it. Every decision the map makes about which CI is at the far end of a cable is made here,
/// so the ladder, its ordering, the ambiguity rule and the two-sided fold are all asserted without
/// infrastructure.
/// </summary>
public sealed class TopologyNeighbourReconcilerTests
{
    private static readonly Guid Switch = Guid.Parse("00000000-0000-0000-0000-0000000000b1");
    private static readonly Guid Router = Guid.Parse("00000000-0000-0000-0000-0000000000b2");
    private static readonly Guid Host = Guid.Parse("00000000-0000-0000-0000-0000000000b3");
    private static readonly Guid Impostor = Guid.Parse("00000000-0000-0000-0000-0000000000b4");

    private static readonly IReadOnlySet<(Guid, Guid)> NothingAsserted = new HashSet<(Guid, Guid)>();

    [Fact]
    public void Reconcile_ASwitchReportingARouterByName_ResolvesTheFarEndToThatCi()
    {
        var reconciliation = TopologyNeighbourReconciler.Reconcile(
            [Report(Switch, "lldp", "GigabitEthernet0/1", "dc1-core-rtr-01", "GigabitEthernet0/24")],
            [Identity(Router, "dc1-core-rtr-01")],
            NothingAsserted);

        var link = Assert.Single(reconciliation.Links);
        Assert.Equal(TopologyNeighbourReconciler.Pair(Switch, Router), (link.SourceCiId, link.TargetCiId));
        Assert.Equal(["lldp"], link.Protocols);
        Assert.Empty(reconciliation.Unresolved);

        // One end reported, which is the normal case and not a weaker link.
        Assert.False(link.ConfirmedByBothEnds);
    }

    /// <summary>
    /// The fold that makes this a reconciler rather than a mapper: one cable, two devices, one link.
    /// </summary>
    [Fact]
    public void Reconcile_BothEndsReportingEachOther_IsOneLinkConfirmedFromBothSides()
    {
        var reconciliation = TopologyNeighbourReconciler.Reconcile(
            [
                Report(Switch, "lldp", "GigabitEthernet0/1", "dc1-core-rtr-01", "GigabitEthernet0/24"),
                Report(Router, "lldp", "GigabitEthernet0/24", "dc1-core-sw-01", "GigabitEthernet0/1"),
            ],
            [Identity(Router, "dc1-core-rtr-01"), Identity(Switch, "dc1-core-sw-01")],
            NothingAsserted);

        var link = Assert.Single(reconciliation.Links);
        Assert.True(link.ConfirmedByBothEnds);

        // Each end's port is the one that end named for itself, whichever order the reports arrived in.
        var (low, _) = TopologyNeighbourReconciler.Pair(Switch, Router);
        Assert.Equal(
            low == Switch ? "GigabitEthernet0/1" : "GigabitEthernet0/24",
            link.SourcePort);
        Assert.Equal(
            low == Switch ? "GigabitEthernet0/24" : "GigabitEthernet0/1",
            link.TargetPort);
    }

    /// <summary>
    /// The ends are ordered by id rather than by who reported, which is exactly what makes the fold
    /// above work: the same cable has the same key however it was seen.
    /// </summary>
    [Fact]
    public void Reconcile_WhicheverEndReportsFirst_ProducesTheSameLinkId()
    {
        var identities = new[] { Identity(Router, "dc1-core-rtr-01"), Identity(Switch, "dc1-core-sw-01") };
        var fromSwitch = TopologyNeighbourReconciler.Reconcile(
            [Report(Switch, "lldp", "Gi0/1", "dc1-core-rtr-01", "Gi0/24")], identities, NothingAsserted);
        var fromRouter = TopologyNeighbourReconciler.Reconcile(
            [Report(Router, "lldp", "Gi0/24", "dc1-core-sw-01", "Gi0/1")], identities, NothingAsserted);

        Assert.Equal(fromSwitch.Links[0].Id, fromRouter.Links[0].Id);
        Assert.Equal(fromSwitch.Links[0].SourceCiId, fromRouter.Links[0].SourceCiId);
    }

    [Fact]
    public void Reconcile_TwoProtocolsAcrossOneCable_CollapseIntoOneLinkNamingBoth()
    {
        var reconciliation = TopologyNeighbourReconciler.Reconcile(
            [
                Report(Switch, "lldp", "Gi0/1", "dc1-core-rtr-01", "Gi0/24"),
                Report(Router, "cdp", "Gi0/24", "dc1-core-sw-01", "Gi0/1"),
            ],
            [Identity(Router, "dc1-core-rtr-01"), Identity(Switch, "dc1-core-sw-01")],
            NothingAsserted);

        var link = Assert.Single(reconciliation.Links);
        Assert.Equal(["cdp", "lldp"], link.Protocols);
    }

    /// <summary>
    /// A management address the far device advertised beats every name, for the reason WP-4.2 gives
    /// for the same ordering: an address is configured on the thing itself, a name is typed by a person.
    /// </summary>
    [Fact]
    public void Reconcile_RemoteAddressAndRemoteNamePointingAtDifferentCis_TakesTheAddress()
    {
        var reconciliation = TopologyNeighbourReconciler.Reconcile(
            [new ObservedNeighbourReport(
                Switch, "DC1 core switch A", "lldp", "Gi0/1", "dc1-core-rtr-01", "Gi0/24", "10.10.0.1")],
            [
                new TopologyCiIdentity(Router, "DC1 core router", ManagementIp: "10.10.0.1"),
                new TopologyCiIdentity(Impostor, "dc1-core-rtr-01"),
            ],
            NothingAsserted);

        var link = Assert.Single(reconciliation.Links);
        Assert.Contains(Router, new[] { link.SourceCiId, link.TargetCiId });
        Assert.DoesNotContain(Impostor, new[] { link.SourceCiId, link.TargetCiId });
    }

    /// <summary>
    /// What a scan already heard a CI call itself outranks what somebody typed into the CMDB, so a
    /// stale CI name cannot outvote the device's own sysName.
    /// </summary>
    [Fact]
    public void Reconcile_SysNameAndCiNameClaimingDifferentCis_TakesTheSysName()
    {
        var reconciliation = TopologyNeighbourReconciler.Reconcile(
            [Report(Switch, "lldp", "Gi0/1", "dc1-core-rtr-01", "Gi0/24")],
            [
                new TopologyCiIdentity(Router, Name: null, SysName: "dc1-core-rtr-01"),
                new TopologyCiIdentity(Impostor, "dc1-core-rtr-01"),
            ],
            NothingAsserted);

        var link = Assert.Single(reconciliation.Links);
        Assert.Contains(Router, new[] { link.SourceCiId, link.TargetCiId });
    }

    [Fact]
    public void Reconcile_AFullyQualifiedRemoteName_MatchesACiRecordedUnderItsShortName()
    {
        var reconciliation = TopologyNeighbourReconciler.Reconcile(
            [Report(Switch, "lldp", "Gi0/1", "dc1-core-rtr-01.corp.example.test", "Gi0/24")],
            [Identity(Router, "dc1-core-rtr-01")],
            NothingAsserted);

        Assert.Single(reconciliation.Links);
        Assert.Empty(reconciliation.Unresolved);
    }

    /// <summary>
    /// A rung that finds two CIs stops the walk, exactly as WP-4.2's ladder does. Drawing a cable to
    /// one of two candidates picks a winner without resolving anything, and the map would state it as
    /// a fact.
    /// </summary>
    [Fact]
    public void Reconcile_ANameTwoCisAnswerTo_IsUnresolvedRatherThanAGuess()
    {
        var reconciliation = TopologyNeighbourReconciler.Reconcile(
            [Report(Switch, "lldp", "Gi0/1", "dc1-core-rtr-01", "Gi0/24")],
            [Identity(Router, "dc1-core-rtr-01"), Identity(Impostor, "dc1-core-rtr-01")],
            NothingAsserted);

        Assert.Empty(reconciliation.Links);
        var unresolved = Assert.Single(reconciliation.Unresolved);
        Assert.Equal(TopologyResolutionFailure.Ambiguous, unresolved.Reason);
        Assert.Equal("dc1-core-rtr-01", unresolved.RemoteSystemName);
    }

    /// <summary>
    /// And it does not fall through to a weaker rung either: a name that two CIs answer to is not
    /// resolved by the fact that only one of them is also a server with that hostname.
    /// </summary>
    [Fact]
    public void Reconcile_AnAmbiguousRung_DoesNotFallThroughToAWeakerOne()
    {
        var reconciliation = TopologyNeighbourReconciler.Reconcile(
            [Report(Switch, "lldp", "Gi0/1", "dc1-esx-01", "Gi0/24")],
            [
                new TopologyCiIdentity(Router, Name: null, SysName: "dc1-esx-01"),
                new TopologyCiIdentity(Impostor, Name: null, SysName: "dc1-esx-01"),
                new TopologyCiIdentity(Host, Name: null, Hostname: "dc1-esx-01"),
            ],
            NothingAsserted);

        Assert.Empty(reconciliation.Links);
        Assert.Equal(TopologyResolutionFailure.Ambiguous, Assert.Single(reconciliation.Unresolved).Reason);
    }

    [Fact]
    public void Reconcile_ANeighbourNoCiAnswersTo_IsReportedAsUnresolvedAndNamesItsReporter()
    {
        var reconciliation = TopologyNeighbourReconciler.Reconcile(
            [Report(Switch, "lldp", "Gi0/9", "some-printer", "eth0")],
            [Identity(Router, "dc1-core-rtr-01")],
            NothingAsserted);

        Assert.Empty(reconciliation.Links);
        var unresolved = Assert.Single(reconciliation.Unresolved);
        Assert.Equal(TopologyResolutionFailure.NoCandidate, unresolved.Reason);
        Assert.Equal(Switch, unresolved.ReportedByCiId);
        Assert.Equal("DC1 core switch A", unresolved.ReportedByCiName);
        Assert.Equal("Gi0/9", unresolved.LocalPort);
    }

    [Fact]
    public void Reconcile_ANeighbourWithNoNameAndNoAddress_SaysItNamedNothing()
    {
        var reconciliation = TopologyNeighbourReconciler.Reconcile(
            [new ObservedNeighbourReport(Switch, "DC1 core switch A", "lldp", "Gi0/9", null, null, null)],
            [Identity(Router, "dc1-core-rtr-01")],
            NothingAsserted);

        Assert.Equal(TopologyResolutionFailure.NoIdentity, Assert.Single(reconciliation.Unresolved).Reason);
    }

    /// <summary>
    /// A stacked switch advertising its own name out of every member port would otherwise draw a
    /// circle on itself. Dropped rather than listed as unresolved — it resolved, to nothing worth drawing.
    /// </summary>
    [Fact]
    public void Reconcile_ADeviceReportingItself_DrawsNoLinkAndIsNotAFinding()
    {
        var reconciliation = TopologyNeighbourReconciler.Reconcile(
            [Report(Switch, "lldp", "Gi0/49", "dc1-core-sw-01", "Gi0/50")],
            [Identity(Switch, "dc1-core-sw-01")],
            NothingAsserted);

        Assert.Empty(reconciliation.Links);
        Assert.Empty(reconciliation.Unresolved);
    }

    /// <summary>
    /// The signal the map draws one line per pair from — and, later, the signal WP-4.6's drift report
    /// reads. Direction is ignored: an operator recording "switch connects to router" and a scan
    /// seeing the router's side of the cable are the same link.
    /// </summary>
    [Fact]
    public void Reconcile_ALinkThatARelationshipAlreadyRecords_SaysSoInEitherDirection()
    {
        var forwards = TopologyNeighbourReconciler.Reconcile(
            [Report(Switch, "lldp", "Gi0/1", "dc1-core-rtr-01", "Gi0/24")],
            [Identity(Router, "dc1-core-rtr-01")],
            new HashSet<(Guid, Guid)> { TopologyNeighbourReconciler.Pair(Switch, Router) });
        var backwards = TopologyNeighbourReconciler.Reconcile(
            [Report(Switch, "lldp", "Gi0/1", "dc1-core-rtr-01", "Gi0/24")],
            [Identity(Router, "dc1-core-rtr-01")],
            new HashSet<(Guid, Guid)> { TopologyNeighbourReconciler.Pair(Router, Switch) });

        Assert.True(forwards.Links[0].MatchesAssertedEdge);
        Assert.True(backwards.Links[0].MatchesAssertedEdge);
    }

    [Fact]
    public void Reconcile_ALinkNoRelationshipRecords_IsFlaggedAsSomethingTheCmdbHasNot()
    {
        var reconciliation = TopologyNeighbourReconciler.Reconcile(
            [Report(Switch, "lldp", "Gi0/2", "dc1-core-rtr-01", "Gi0/24")],
            [Identity(Router, "dc1-core-rtr-01")],
            new HashSet<(Guid, Guid)> { TopologyNeighbourReconciler.Pair(Host, Router) });

        Assert.False(Assert.Single(reconciliation.Links).MatchesAssertedEdge);
    }

    private static ObservedNeighbourReport Report(
        Guid reporter,
        string protocol,
        string? localPort,
        string? remoteSystemName,
        string? remotePort) =>
        new(reporter, "DC1 core switch A", protocol, localPort, remoteSystemName, remotePort, null);

    private static TopologyCiIdentity Identity(Guid ciId, string name) => new(ciId, name);
}
