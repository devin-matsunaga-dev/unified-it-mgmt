using Modules.Assets.Data;
using Modules.Assets.Features.Drift;

namespace Infrastructure.Tests;

/// <summary>
/// The comparator that decides where the CMDB and the network disagree. No database, no clock — the
/// instant is handed in — so the whole matrix is exercised here rather than through the API.
/// </summary>
public sealed class DriftAnalyzerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Analyse_WhenTheRecordedSiteAndTheReportedLocationDisagree_IsAChangedLocation()
    {
        var findings = Analyse(Network(siteName: "Head Office"), Observed(sysLocation: "Primary Data Centre"));

        var finding = Assert.Single(findings, item => item.Field == DriftFields.Location);
        Assert.Equal(DriftFindingKind.Changed, finding.Kind);
        Assert.Equal("Head Office", finding.RecordedValue);
        Assert.Equal("Primary Data Centre", finding.ObservedValue);
    }

    /// <summary>
    /// Case, surrounding space and runs of inner whitespace are two people typing one place, not an
    /// asset that moved. Reporting them would bury the CI that genuinely changed building.
    /// </summary>
    [Theory]
    [InlineData("Primary Data Centre", "primary data centre")]
    [InlineData("Primary Data Centre", "  Primary Data Centre  ")]
    [InlineData("Primary Data Centre", "Primary  Data   Centre")]
    public void Analyse_WhenTheTwoLocationsDifferOnlyInCaseOrSpacing_ReportsNothing(string recorded, string observed)
    {
        Assert.Empty(Analyse(Network(siteName: recorded), Observed(sysLocation: observed)));
    }

    [Fact]
    public void Analyse_WhenTheCmdbHasNoSiteAndTheDeviceReportsOne_IsANewLocation()
    {
        var findings = Analyse(Network(siteName: null), Observed(sysLocation: "Regional Branch"));

        var finding = Assert.Single(findings);
        Assert.Equal(DriftFindingKind.New, finding.Kind);
        Assert.Null(finding.RecordedValue);
        Assert.Equal("Regional Branch", finding.ObservedValue);
    }

    /// <summary>
    /// A device that answered SNMP and left the field empty is making a statement. That is the whole
    /// difference between this case and the next one.
    /// </summary>
    [Fact]
    public void Analyse_WhenADeviceThatAnsweredSnmpReportsNoLocation_IsAMissingLocation()
    {
        var findings = Analyse(
            Network(siteName: "Head Office"),
            Observed(sysLocation: null, sysName: "hq-acc-sw-01"));

        var finding = Assert.Single(findings);
        Assert.Equal(DriftFindingKind.Missing, finding.Kind);
        Assert.Equal("Head Office", finding.RecordedValue);
        Assert.Null(finding.ObservedValue);
    }

    /// <summary>
    /// The gate that keeps the report readable. Without it every address that answers a ping and
    /// nothing else reports a missing everything — hundreds of findings that all say "this device does
    /// not run an SNMP agent".
    /// </summary>
    [Fact]
    public void Analyse_ForADeviceThatOnlyAnsweredAPing_ReportsNoMissingFieldsAtAll()
    {
        var findings = Analyse(
            Network(siteName: "Head Office"),
            Observed(sysLocation: null, sysName: null, sysDescription: null));

        Assert.Empty(findings);
    }

    [Fact]
    public void Analyse_WhenTheRecordedManagementIpIsNotWhereTheDeviceAnswered_IsAChangedAddress()
    {
        var findings = Analyse(
            Network(siteName: "Head Office", managementIp: "10.20.0.3"),
            Observed(sysLocation: "Head Office", address: "10.20.0.53"));

        var finding = Assert.Single(findings, item => item.Field == DriftFields.ManagementIp);
        Assert.Equal(DriftFindingKind.Changed, finding.Kind);
        Assert.Equal("10.20.0.3", finding.RecordedValue);
        Assert.Equal("10.20.0.53", finding.ObservedValue);
    }

    /// <summary>
    /// A hostname compares on its leftmost label: a resolver answers with a domain attached while a CI
    /// records the short name somebody typed, and comparing the two unshortened matches nothing.
    /// </summary>
    [Fact]
    public void Analyse_WhenTheRecordedHostnameIsTheFullyQualifiedFormOfTheReportedOne_ReportsNothing()
    {
        Assert.Empty(Analyse(
            Server(hostname: "dc1-esx-01.corp.local"),
            Observed(sysLocation: null, sysName: "dc1-esx-01", sysDescription: null)));
    }

    [Fact]
    public void Analyse_WhenTheDeviceCallsItselfSomethingElse_IsAChangedHostname()
    {
        var findings = Analyse(
            Server(hostname: "dc1-esx-01.corp.local"),
            Observed(sysLocation: null, sysName: "dc1-esx-99"));

        var finding = Assert.Single(findings, item => item.Field == DriftFields.Hostname);
        Assert.Equal(DriftFindingKind.Changed, finding.Kind);
        Assert.Equal("dc1-esx-99", finding.ObservedValue);
    }

    /// <summary>
    /// A type that records no hostname must not be told its hostname is missing. TPH makes every column
    /// physically nullable, so this is the same "the schema lives above the database" rule
    /// <c>CiTypeSchema</c> exists for.
    /// </summary>
    [Fact]
    public void Analyse_ForASwitch_NeverReportsAHostnameOrForAServerAManagementIp()
    {
        var switchFindings = Analyse(
            Network(siteName: "Primary Data Centre"),
            Observed(sysLocation: "Primary Data Centre", sysName: "dc1-core-sw-01"));
        var serverFindings = Analyse(
            Server(hostname: "dc1-db-01.corp.local"),
            Observed(sysLocation: null, sysName: "dc1-db-01"));

        Assert.DoesNotContain(switchFindings, finding => finding.Field == DriftFields.Hostname);
        Assert.DoesNotContain(serverFindings, finding => finding.Field == DriftFields.ManagementIp);
    }

    /// <summary>
    /// What "missing" means to somebody reconciling an estate: the record is still here and the thing
    /// it describes has stopped answering.
    /// </summary>
    [Fact]
    public void Analyse_ForACiNoScanHasSeenForLongerThanTheThreshold_IsAMissingLastSeen()
    {
        var findings = Analyse(
            Network(siteName: "Regional Branch"),
            Observed(sysLocation: "Regional Branch", lastSeenAt: Now.AddDays(-32)));

        var finding = Assert.Single(findings, item => item.Field == DriftFields.LastSeen);
        Assert.Equal(DriftFindingKind.Missing, finding.Kind);
        Assert.Null(finding.RecordedValue);
    }

    [Fact]
    public void Analyse_ForACiSeenInsideTheThreshold_ReportsNothingAboutItsLastSighting()
    {
        var findings = Analyse(
            Network(siteName: "Regional Branch"),
            Observed(sysLocation: "Regional Branch", lastSeenAt: Now.AddDays(-6)));

        Assert.DoesNotContain(findings, finding => finding.Field == DriftFields.LastSeen);
    }

    /// <summary>Every finding is against one CI, and one CI can carry several of them.</summary>
    [Fact]
    public void Analyse_ForACiThatDisagreesInSeveralWays_ReportsEachFieldSeparately()
    {
        var findings = Analyse(
            Network(siteName: "Head Office", managementIp: "10.20.0.3"),
            Observed(sysLocation: "Primary Data Centre", address: "10.20.0.53", lastSeenAt: Now.AddDays(-40)));

        Assert.Equal(
            [DriftFields.LastSeen, DriftFields.Location, DriftFields.ManagementIp],
            findings.Select(finding => finding.Field).Order(StringComparer.Ordinal));
    }

    private static IReadOnlyList<DriftFinding> Analyse(DriftSubject subject, DriftObservation observation) =>
        DriftAnalyzer.Analyse(subject with { Observation = observation }, Now, DriftAnalyzer.DefaultStaleAfterDays);

    private static DriftSubject Network(string? siteName, string managementIp = "10.10.0.2") =>
        new(Guid.CreateVersion7(), "Core switch", CiType.NetworkDevice, Guid.CreateVersion7(), siteName,
            RecordedHostname: null, RecordedManagementIp: managementIp, Observation: Observed(null));

    private static DriftSubject Server(string hostname) =>
        new(Guid.CreateVersion7(), "Hypervisor host", CiType.Server, Guid.CreateVersion7(), SiteName: null,
            RecordedHostname: hostname, RecordedManagementIp: null, Observation: Observed(null));

    private static DriftObservation Observed(
        string? sysLocation,
        string address = "10.10.0.2",
        string? sysName = "dc1-core-sw-01",
        string? sysDescription = "Cisco IOS Software",
        DateTimeOffset? lastSeenAt = null) =>
        new(address, null, sysName, sysLocation, sysDescription,
            AnsweredSnmp: sysName is not null || sysDescription is not null,
            LastSeenAt: lastSeenAt ?? Now.AddMinutes(-5));
}
