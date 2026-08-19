using Contracts.Events;

using Modules.Assets.Data;
using Modules.Assets.Features.Discovery;

namespace Infrastructure.Tests;

/// <summary>
/// The match ladder, with no database anywhere near it. This is the whole of "auto-update matched,
/// queue unmatched": every decision the pipeline makes about whether a discovery is already a CI is
/// made here, so the rungs, their order and the ambiguity rule are all asserted without infrastructure.
/// </summary>
public sealed class DiscoveryMatcherTests
{
    private static readonly Guid Router = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid Switch = Guid.Parse("00000000-0000-0000-0000-0000000000a2");
    private static readonly Guid Host = Guid.Parse("00000000-0000-0000-0000-0000000000a3");
    private static readonly Guid Monitored = Guid.Parse("00000000-0000-0000-0000-0000000000a4");

    [Fact]
    public void Match_AddressAlreadyMonitored_WinsOverEveryCmdbRung()
    {
        var fingerprint = new DiscoveryFingerprint("10.10.0.1", "dc1-core-rtr-01", "dc1-core-rtr-01");
        var candidates = new[]
        {
            new CiMatchCandidate(Router, "DC1 core router", CiType.NetworkDevice, "10.10.0.1"),
        };

        var match = DiscoveryMatcher.Match(fingerprint, candidates, monitoredCiId: Monitored);

        // Not the CI whose management IP matches: a monitored device is a statement an operator made
        // deliberately, and a management IP is a field somebody typed once and may never have revisited.
        Assert.Equal(Monitored, match.CiId);
        Assert.Equal(DiscoveryMatchRule.MonitoredAddress, match.Rule);
    }

    [Fact]
    public void Match_ManagementIpRecordedOnANetworkCi_MatchesThatCi()
    {
        var fingerprint = new DiscoveryFingerprint("10.10.0.2", null, null);
        var candidates = new[]
        {
            new CiMatchCandidate(Switch, "DC1 core switch A", CiType.NetworkDevice, "10.10.0.2"),
        };

        var match = DiscoveryMatcher.Match(fingerprint, candidates, monitoredCiId: null);

        Assert.Equal(Switch, match.CiId);
        Assert.Equal(DiscoveryMatchRule.ManagementIp, match.Rule);
    }

    [Fact]
    public void Match_HostnameRecordedOnAServer_MatchesOnSysNameAndOnShortReverseDns()
    {
        var candidates = new[]
        {
            new CiMatchCandidate(Host, "DC1 host 1", CiType.Server, null, "dc1-esx-01"),
        };

        var bySysName = DiscoveryMatcher.Match(
            new DiscoveryFingerprint("172.18.0.9", null, "dc1-esx-01"), candidates, null);
        var byHostname = DiscoveryMatcher.Match(
            new DiscoveryFingerprint("172.18.0.9", "dc1-esx-01", null), candidates, null);

        Assert.Equal(Host, bySysName.CiId);
        Assert.Equal(DiscoveryMatchRule.Hostname, bySysName.Rule);
        Assert.Equal(Host, byHostname.CiId);
        Assert.Equal(DiscoveryMatchRule.Hostname, byHostname.Rule);
    }

    [Fact]
    public void Match_CiNamedExactlyWhatTheDeviceCallsItself_MatchesOnTheWeakestRung()
    {
        var fingerprint = new DiscoveryFingerprint("172.18.0.7", null, "sim-switch-healthy");
        var candidates = new[]
        {
            new CiMatchCandidate(Switch, "sim-switch-healthy", CiType.NetworkDevice),
        };

        var match = DiscoveryMatcher.Match(fingerprint, candidates, null);

        Assert.Equal(Switch, match.CiId);
        Assert.Equal(DiscoveryMatchRule.Name, match.Rule);
    }

    [Fact]
    public void Match_ACiNameThatMerelyStartsTheSame_DoesNotMatch()
    {
        // "dc1-core-sw-01" must not match "dc1-core-sw-010". A prefix comparison would silently
        // attribute a scan of one switch to a different one on any estate that numbers past nine.
        var fingerprint = new DiscoveryFingerprint("172.18.0.7", null, "dc1-core-sw-01");
        var candidates = new[]
        {
            new CiMatchCandidate(Switch, "dc1-core-sw-010", CiType.NetworkDevice),
        };

        Assert.Equal(DiscoveryMatchRule.None, DiscoveryMatcher.Match(fingerprint, candidates, null).Rule);
    }

    [Fact]
    public void Match_TwoCisRecordingTheSameManagementIp_IsAmbiguousAndMatchesNeither()
    {
        var fingerprint = new DiscoveryFingerprint("10.10.0.1", null, null);
        var candidates = new[]
        {
            new CiMatchCandidate(Router, "DC1 core router", CiType.NetworkDevice, "10.10.0.1"),
            new CiMatchCandidate(Switch, "Old core router", CiType.NetworkDevice, "10.10.0.1"),
        };

        var match = DiscoveryMatcher.Match(fingerprint, candidates, null);

        Assert.Null(match.CiId);
        Assert.Equal(DiscoveryMatchRule.Ambiguous, match.Rule);
        Assert.Equal([Router, Switch], match.Contenders.Order().ToArray());
    }

    [Fact]
    public void Match_AmbiguousOnAStrongRung_DoesNotFallThroughToAWeakerOne()
    {
        // Both CIs claim the address; exactly one is also named after the device. Falling through would
        // resolve the tie with the weaker signal, which answers a question nobody asked — the estate
        // still contains two CIs claiming one management IP and a human has to fix that.
        var fingerprint = new DiscoveryFingerprint("10.10.0.1", null, "dc1-core-rtr-01");
        var candidates = new[]
        {
            new CiMatchCandidate(Router, "dc1-core-rtr-01", CiType.NetworkDevice, "10.10.0.1"),
            new CiMatchCandidate(Switch, "Decommissioned router", CiType.NetworkDevice, "10.10.0.1"),
        };

        var match = DiscoveryMatcher.Match(fingerprint, candidates, null);

        Assert.Equal(DiscoveryMatchRule.Ambiguous, match.Rule);
        Assert.Null(match.CiId);
    }

    [Fact]
    public void Match_NothingInTheCmdbClaimsIt_IsTheReviewQueuesCase()
    {
        var fingerprint = new DiscoveryFingerprint("172.18.0.42", "stranger", "stranger");

        var match = DiscoveryMatcher.Match(fingerprint, [], null);

        Assert.Null(match.CiId);
        Assert.Equal(DiscoveryMatchRule.None, match.Rule);
        Assert.Empty(match.Contenders);
    }

    [Fact]
    public void Match_ADiscoveryWithNoNameAtAll_MatchesNothingByName()
    {
        // A ping-only host: no SNMP, no reverse DNS. WP-4.1's walk found eleven of these on the session
        // network, and the name rungs must not match every unnamed CI to every one of them.
        var fingerprint = new DiscoveryFingerprint("172.18.0.13", null, null);
        var candidates = new[] { new CiMatchCandidate(Host, string.Empty, CiType.Hardware, null, string.Empty) };

        Assert.Equal(DiscoveryMatchRule.None, DiscoveryMatcher.Match(fingerprint, candidates, null).Rule);
    }

    [Theory]
    [InlineData("sim-switch-healthy.example.test", "sim-switch-healthy")]
    [InlineData("DC1-ESX-01.corp.example", "dc1-esx-01")]
    [InlineData("plainname", "plainname")]
    [InlineData("  padded.example.  ", "padded")]
    public void ShortHostname_AFullyQualifiedName_IsItsFirstLabelLowercased(string input, string expected) =>
        Assert.Equal(expected, DiscoveryIdentity.ShortHostname(input));

    [Theory]
    [InlineData("172.18.0.7")]
    [InlineData("10.0.0.1")]
    [InlineData("")]
    [InlineData(null)]
    public void ShortHostname_AnAddressOrNothing_IsNotAHostname(string? input) =>
        // Splitting "172.18.0.7" on the first dot yields "172", which every device in a /8 would share.
        Assert.Null(DiscoveryIdentity.ShortHostname(input));

    [Fact]
    public void KeyFor_TheTiers_PreferTheMostStableIdentityAvailable()
    {
        Assert.Equal("snmp:sim-switch-healthy", DiscoveryIdentity.KeyFor(
            DiscoveryIdentity.FingerprintOf(Discovery("172.18.0.7", "sim-switch-healthy.example.test", "sim-switch-healthy"))));
        Assert.Equal("host:sim-switch-healthy", DiscoveryIdentity.KeyFor(
            DiscoveryIdentity.FingerprintOf(Discovery("172.18.0.7", "sim-switch-healthy.example.test", null))));
        Assert.Equal("addr:172.18.0.7", DiscoveryIdentity.KeyFor(
            DiscoveryIdentity.FingerprintOf(Discovery("172.18.0.7", null, null))));
    }

    [Fact]
    public void Names_ASysNameAndAMatchingHostname_AreNotOfferedTwice()
    {
        var fingerprint = new DiscoveryFingerprint("172.18.0.7", "sim-switch-healthy", "sim-switch-healthy");

        Assert.Equal(["sim-switch-healthy"], fingerprint.Names);
    }

    [Fact]
    public void Names_ASysNameThatDisagreesWithReverseDns_OffersBothWithSysNameFirst()
    {
        var fingerprint = new DiscoveryFingerprint("172.18.0.7", "dhcp-172-18-0-7", "sim-switch-healthy");

        Assert.Equal(["sim-switch-healthy", "dhcp-172-18-0-7"], fingerprint.Names);
    }

    private static DeviceDiscovered Discovery(string address, string? hostname, string? sysName) => new(
        Guid.CreateVersion7(),
        DateTimeOffset.UtcNow,
        "discovery-1",
        Guid.CreateVersion7(),
        "Local subnet sweep",
        Guid.CreateVersion7(),
        address,
        hostname,
        hostname is null ? null : "dns",
        RespondedToPing: true,
        OpenPorts: [],
        Snmp: sysName is null ? null : new DiscoveredSnmpIdentity(sysName, null, null, null, null, null),
        Neighbours: []);
}
