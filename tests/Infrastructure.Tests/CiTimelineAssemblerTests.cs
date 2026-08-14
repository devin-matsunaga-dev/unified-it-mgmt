using Modules.Assets.Data;
using Modules.Assets.Features.Timeline;

using Platform.Auditing;
using Platform.Integration;

namespace Infrastructure.Tests;

/// <summary>
/// The interleaving, the filter and the wording, against hand-written history. No database and no clock,
/// following <c>ImpactAnalyzerTests</c>: everything this feature decides — what order four sources come
/// back in, what a row says, and what a capped source is allowed to claim — is decided here.
/// <para>
/// The fixture is one server that was registered, deployed, checked out, alerted twice and had a ticket
/// raised about it: the shape of a real asset's first fortnight, and the shape the WP's own verification
/// step walks.
/// </para>
/// </summary>
public sealed class CiTimelineAssemblerTests
{
    private static readonly Guid CiId = Guid.Parse("0198b000-0000-7000-8000-000000000001");
    private static readonly Guid AlertOldId = Guid.Parse("0198b000-0000-7000-8000-00000000a001");
    private static readonly Guid AlertNewId = Guid.Parse("0198b000-0000-7000-8000-00000000a002");
    private static readonly Guid DeviceId = Guid.Parse("0198b000-0000-7000-8000-00000000d001");
    private static readonly Guid TicketId = Guid.Parse("0198b000-0000-7000-8000-00000000e001");
    private static readonly Guid TransitionId = Guid.Parse("0198b000-0000-7000-8000-00000000c001");
    private static readonly Guid AssignmentId = Guid.Parse("0198b000-0000-7000-8000-00000000c002");
    private static readonly Guid AuditId = Guid.Parse("0198b000-0000-7000-8000-00000000f001");

    private static readonly DateTimeOffset Day1 = new(2026, 8, 1, 9, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The WP's own verification step: a device with a seeded history shows correctly ordered mixed
    /// events. Four sources, one axis, newest first.
    /// </summary>
    [Fact]
    public void Assemble_ForACiWithHistoryFromEverySource_InterleavesThemNewestFirst()
    {
        var timeline = CiTimelineAssembler.Assemble(History(), limit: 50);

        Assert.Equal(
            [
                CiTimelineEventKind.Alert,      // day 12 — still open
                CiTimelineEventKind.Ticket,     // day 10
                CiTimelineEventKind.Alert,      // day 8 — long since recovered
                CiTimelineEventKind.Config,     // day 5 — somebody edited the record
                CiTimelineEventKind.Lifecycle,  // day 3 — the check-out
                CiTimelineEventKind.Lifecycle,  // day 2 — the move into service
            ],
            timeline.Entries.Select(entry => entry.Kind));

        // Descending, strictly: the ordering is the feature, and a single pair out of sequence makes the
        // whole axis a lie.
        Assert.Equal(
            timeline.Entries.Select(entry => entry.OccurredAt).OrderByDescending(at => at),
            timeline.Entries.Select(entry => entry.OccurredAt));
    }

    /// <summary>
    /// The other half of the WP's verification: "alerts only" works. It is not a rendering filter — the
    /// other three sources are reported as unrequested, so a browser can say "not shown" rather than
    /// leaving an operator to read an empty section as an asset nothing has ever happened to.
    /// </summary>
    [Fact]
    public void Assemble_FilteredToAlerts_ReturnsOnlyAlertsAndSaysTheOthersWereNotAsked()
    {
        var timeline = CiTimelineAssembler.Assemble(
            History() with { Kinds = [CiTimelineEventKind.Alert] }, limit: 50);

        Assert.All(timeline.Entries, entry => Assert.Equal(CiTimelineEventKind.Alert, entry.Kind));
        Assert.Equal(2, timeline.Summary.EntryCount);
        Assert.Equal([CiTimelineEventKind.Alert], timeline.Kinds);

        var ticketSource = Assert.Single(
            timeline.Sources, source => source.Kind == CiTimelineEventKind.Ticket);
        Assert.False(ticketSource.Requested);
        Assert.Equal(0, ticketSource.Total);
    }

    /// <summary>
    /// An alert is one row on the axis, at the moment it was raised, and its recovery is stated on that
    /// row. Two rows would double a noisy device's history and put the recovery above the fault.
    /// </summary>
    [Fact]
    public void Assemble_ForAClearedAlert_IsOneEntryAtTheMomentItWasRaised_StatingHowLongItLasted()
    {
        var timeline = CiTimelineAssembler.Assemble(History(), limit: 50);

        var cleared = Assert.Single(timeline.Entries, entry => entry.AlertId == AlertOldId);
        Assert.Equal(Day1.AddDays(7), cleared.OccurredAt);
        Assert.Contains("recovered after 25 minutes", cleared.Detail, StringComparison.Ordinal);
        Assert.Equal("Cleared", cleared.Status);
    }

    /// <summary>An alert still open says so, rather than reporting a duration it does not have yet.</summary>
    [Fact]
    public void Assemble_ForAnOpenAlert_SaysItIsStillOpen()
    {
        var timeline = CiTimelineAssembler.Assemble(History(), limit: 50);

        var open = Assert.Single(timeline.Entries, entry => entry.AlertId == AlertNewId);
        Assert.Contains("still open", open.Detail, StringComparison.Ordinal);
        Assert.Equal("Critical", open.Severity);
    }

    /// <summary>
    /// WP-5.1's suppressed alerts are on the timeline, not hidden by it. Suppression withheld the
    /// *message*; the alert was real and recorded, and "was this machine affected on Tuesday" has to
    /// answer yes. The row says why nobody was told.
    /// </summary>
    [Fact]
    public void Assemble_ForAnAlertSuppressedUnderItsRootCause_ShowsItAndSaysWhyNobodyWasTold()
    {
        var timeline = CiTimelineAssembler.Assemble(History(), limit: 50);

        var suppressed = Assert.Single(timeline.Entries, entry => entry.AlertId == AlertNewId);
        Assert.Contains("suppressed under its root cause", suppressed.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// A ticket sits at the moment it was raised. Where somebody attached it to this asset materially
    /// later, the entry carries that instant so the browser can say so — the alternative is a timeline
    /// that quietly claims the asset was implicated from the start.
    /// </summary>
    [Fact]
    public void Assemble_ForATicketLinkedLongAfterItWasRaised_KeepsTheRaisedMomentAndCarriesTheLinkedOne()
    {
        var timeline = CiTimelineAssembler.Assemble(History(), limit: 50);

        var ticket = Assert.Single(timeline.Entries, entry => entry.TicketId == TicketId);
        Assert.Equal(Day1.AddDays(9), ticket.OccurredAt);
        Assert.Equal(Day1.AddDays(9).AddHours(6), ticket.LinkedAt);
        Assert.Equal("INC-000042", ticket.TicketNumber);
        Assert.Equal("High", ticket.Priority);
    }

    /// <summary>
    /// The normal case is the opposite one: a ticket triaged onto its asset within the minute has nothing
    /// to point out, and a row on every ticket saying so would be noise.
    /// </summary>
    [Fact]
    public void Assemble_ForATicketLinkedWhenItWasRaised_PointsAtNoSeparateLinkTime()
    {
        var history = History();
        var ticket = history.Tickets.Tickets[0] with { LinkedAt = history.Tickets.Tickets[0].CreatedAt };
        var timeline = CiTimelineAssembler.Assemble(
            history with { Tickets = new CiTicketHistory([ticket], 1) }, limit: 50);

        Assert.Null(Assert.Single(timeline.Entries, entry => entry.TicketId == TicketId).LinkedAt);
    }

    /// <summary>
    /// Both lifecycle tables land under one kind, and each reads as the sentence the CI page already
    /// uses for it.
    /// </summary>
    [Fact]
    public void Assemble_ForALifecycleMoveAndACheckOut_ReadsBothAsPlainEnglishUnderOneKind()
    {
        var timeline = CiTimelineAssembler.Assemble(History(), limit: 50);

        var lifecycle = timeline.Entries
            .Where(entry => entry.Kind == CiTimelineEventKind.Lifecycle)
            .ToList();

        Assert.Equal(2, lifecycle.Count);
        Assert.Contains(lifecycle, entry => entry.Title == "Alex Doe took it out (Finance · Head Office)");
        Assert.Contains(lifecycle, entry => entry.Title == "In stock → Deployed");
        Assert.Equal("Racked in DC1.", Assert.Single(lifecycle, entry => entry.Title == "In stock → Deployed").Detail);
    }

    /// <summary>
    /// An audited edit names the fields it changed. "Updated" six times in a row is what the audit log
    /// says and it tells an operator nothing at all.
    /// </summary>
    [Fact]
    public void Assemble_ForAnAuditedEdit_NamesTheFieldsThatActuallyChanged()
    {
        var timeline = CiTimelineAssembler.Assemble(History(), limit: 50);

        var edit = Assert.Single(timeline.Entries, entry => entry.Kind == CiTimelineEventKind.Config);
        Assert.Equal("Record updated", edit.Title);
        Assert.Equal("Changed name, ownership.siteName.", edit.Detail);
        Assert.Equal("alex", edit.Actor);
    }

    /// <summary>
    /// The failure path this feature is most likely to meet in production: an audit row whose documents
    /// are not the shape anybody expects. The entry still renders — losing one edit's field list is a
    /// blemish, and losing the whole timeline with it is an outage.
    /// </summary>
    [Fact]
    public void Assemble_ForAnAuditRowWithUnreadableDocuments_StillRendersTheEntryWithNoFieldList()
    {
        var history = History();
        var broken = history.Audit.Entries[0] with { BeforeJson = "{not json", AfterJson = "{\"name\":\"x\"}" };
        var timeline = CiTimelineAssembler.Assemble(
            history with { Audit = new AuditTrail([broken], 1) }, limit: 50);

        var edit = Assert.Single(timeline.Entries, entry => entry.Kind == CiTimelineEventKind.Config);
        Assert.Equal("Record updated", edit.Title);
        Assert.Null(edit.Detail);
    }

    /// <summary>
    /// A creation has no before and a deletion has no after, so neither names changed fields. Diffing
    /// against nothing would report every field of the record as "changed", which is true of a creation
    /// and useless on a timeline.
    /// </summary>
    [Fact]
    public void Assemble_ForACreation_NamesNoChangedFields()
    {
        var history = History();
        var created = history.Audit.Entries[0] with { Action = "Created", BeforeJson = null };
        var timeline = CiTimelineAssembler.Assemble(
            history with { Audit = new AuditTrail([created], 1) }, limit: 50);

        var entry = Assert.Single(timeline.Entries, entry => entry.Kind == CiTimelineEventKind.Config);
        Assert.Equal("Registered in the CMDB", entry.Title);
        Assert.Null(entry.Detail);
    }

    /// <summary>
    /// An action this module does not recognise prints as itself. The audit log is written by every module
    /// in the platform, and renaming what it does not understand is how a timeline starts lying.
    /// </summary>
    [Fact]
    public void Assemble_ForAnAuditedActionItDoesNotRecognise_PrintsTheActionItself()
    {
        var history = History();
        var odd = history.Audit.Entries[0] with { Action = "Reconciled" };
        var timeline = CiTimelineAssembler.Assemble(
            history with { Audit = new AuditTrail([odd], 1) }, limit: 50);

        Assert.Equal("Reconciled", Assert.Single(
            timeline.Entries, entry => entry.Kind == CiTimelineEventKind.Config).Title);
    }

    /// <summary>
    /// A capped source states what it is holding back, per source rather than for the whole axis. This is
    /// the property the per-source cap exists for: the alert list truncating must not make the reader
    /// doubt that every ticket is on screen.
    /// </summary>
    [Fact]
    public void Assemble_WhenOneSourceIsCapped_SaysSoOnThatSourceAndCountsTheRestWhole()
    {
        var history = History();
        var timeline = CiTimelineAssembler.Assemble(
            history with { Alerts = new CiAlertHistory(history.Alerts.Alerts, Total: 400) }, limit: 2);

        var alerts = Assert.Single(timeline.Sources, source => source.Kind == CiTimelineEventKind.Alert);
        Assert.True(alerts.Truncated);
        Assert.Equal(400, alerts.Total);
        Assert.Equal(2, alerts.Returned);

        var tickets = Assert.Single(timeline.Sources, source => source.Kind == CiTimelineEventKind.Ticket);
        Assert.False(tickets.Truncated);

        Assert.True(timeline.Summary.Truncated);
        // Every source's real total, not the number of rows on screen.
        Assert.Equal(400 + 1 + 2 + 1, timeline.Summary.TotalCount);
    }

    /// <summary>
    /// A source can never claim a total below the rows it just handed over, whatever it says its total
    /// is. The summary and the axis underneath it must not contradict each other.
    /// </summary>
    [Fact]
    public void Assemble_WhenASourceUnderstatesItsOwnTotal_ReportsWhatItActuallyReturned()
    {
        var history = History();
        var timeline = CiTimelineAssembler.Assemble(
            history with { Alerts = new CiAlertHistory(history.Alerts.Alerts, Total: 0) }, limit: 50);

        var alerts = Assert.Single(timeline.Sources, source => source.Kind == CiTimelineEventKind.Alert);
        Assert.Equal(2, alerts.Total);
        Assert.False(alerts.Truncated);
    }

    /// <summary>
    /// The window an empty timeline covers. A CI nothing has ever happened to has no earliest and no
    /// latest — reporting zeroes or "now" would be inventing history.
    /// </summary>
    [Fact]
    public void Assemble_ForACiWithNoHistoryAtAll_ReportsAnEmptyAxisAndNoDates()
    {
        var timeline = CiTimelineAssembler.Assemble(
            new CiTimelineSubject(
                CiId, "Standalone jump box", [], null, null,
                new CiAlertHistory([], 0), new CiTicketHistory([], 0), [], 0, new AuditTrail([], 0)),
            limit: 50);

        Assert.Empty(timeline.Entries);
        Assert.Equal(0, timeline.Summary.TotalCount);
        Assert.False(timeline.Summary.Truncated);
        Assert.Null(timeline.Summary.EarliestAt);
        Assert.Null(timeline.Summary.LatestAt);
        // An unfiltered request asks for everything, and the response says so rather than echoing the
        // empty list it was given.
        Assert.Equal(CiTimelineAssembler.AllKinds, timeline.Kinds);
    }

    /// <summary>
    /// One server's first fortnight: registered and moved into service, checked out, edited, alerted
    /// twice, and one ticket raised about it and linked to it six hours later.
    /// </summary>
    private static CiTimelineSubject History() => new(
        CiId,
        "DC1 hypervisor host 1",
        [],
        From: null,
        To: null,
        new CiAlertHistory(
            [
                new CiAlertHistoryEntry(
                    AlertNewId, DeviceId, "10.10.0.21", "check:cpu", "cpu.percent",
                    "Critical", "Open", "CPU above 90%", "RootCause",
                    Day1.AddDays(11), ClearedAt: null, AcknowledgedAt: null, AcknowledgedByName: null),
                new CiAlertHistoryEntry(
                    AlertOldId, DeviceId, "10.10.0.21", "check:availability", "check.success",
                    "Warning", "Cleared", "No response to ICMP", "None",
                    Day1.AddDays(7), Day1.AddDays(7).AddMinutes(25), null, null),
            ],
            Total: 2),
        new CiTicketHistory(
            [
                new CiTicketHistoryEntry(
                    TicketId, "INC-000042", "ERP is unreachable", "In progress", "High", "Incident",
                    "Sam Roe", Day1.AddDays(9), Day1.AddDays(9).AddHours(6)),
            ],
            Total: 1),
        [
            new CiLifecycleEvent(
                AssignmentId, Day1.AddDays(2), "sam", null, null,
                CiAssignmentAction.CheckOut, null, "Alex Doe", "Finance", "Head Office", null),
            new CiLifecycleEvent(
                TransitionId, Day1.AddDays(1), "alex",
                CiLifecycleState.InStock, CiLifecycleState.Deployed,
                Action: null, null, null, null, null, "Racked in DC1."),
        ],
        LifecycleTotal: 2,
        new AuditTrail(
            [
                new AuditTrailEntry(
                    AuditId,
                    "alex",
                    "Updated",
                    """{"name":"esx-01","ownership":{"ownerName":"Alex Doe","siteName":"Head Office"}}""",
                    """{"name":"dc1-esx-01","ownership":{"ownerName":"Alex Doe","siteName":"DC1"}}""",
                    Day1.AddDays(4),
                    "correlation-1"),
            ],
            Total: 1));
}
