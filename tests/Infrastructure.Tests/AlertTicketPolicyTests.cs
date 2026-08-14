using Contracts.Events;

using Modules.Helpdesk.Data;
using Modules.Helpdesk.Features.AlertTickets;
using Modules.Helpdesk.Features.Tickets;

using Platform.Integration;

namespace Infrastructure.Tests;

/// <summary>
/// What an alert reads like once it is a ticket, with no infrastructure in the way. The dedupe key is
/// the part that matters most: WP-3.5 derived its rule ids from the check id precisely so this string
/// is identical across a restart and on every recurrence, and the whole "one ticket per alert" rule
/// rests on that.
/// </summary>
public sealed class AlertTicketPolicyTests
{
    private static readonly Guid DeviceId = Guid.Parse("3fa85f64-5717-4562-b3fc-2c963f66afa6");

    [Fact]
    public void DedupeKey_ForADeviceAndRule_IsTheKeyArchitectureNames()
    {
        var key = AlertTicketPolicy.DedupeKey(DeviceId, "check:0199-abc:availability");

        Assert.Equal("alert:3fa85f64-5717-4562-b3fc-2c963f66afa6:check:0199-abc:availability", key);
    }

    /// <summary>The same problem twice is the same key — otherwise nothing downstream dedupes.</summary>
    [Fact]
    public void DedupeKey_ForTheSameProblemTwice_IsIdentical() =>
        Assert.Equal(
            AlertTicketPolicy.DedupeKey(DeviceId, "check:1:cpu.utilisation_percent"),
            AlertTicketPolicy.DedupeKey(DeviceId, "check:1:cpu.utilisation_percent"));

    [Fact]
    public void DedupeKey_ForTwoRulesOnOneDevice_Differs() =>
        Assert.NotEqual(
            AlertTicketPolicy.DedupeKey(DeviceId, "check:1:availability"),
            AlertTicketPolicy.DedupeKey(DeviceId, "check:1:cpu.utilisation_percent"));

    /// <summary>An empty rule id would collapse every problem on a device into one ticket.</summary>
    [Fact]
    public void DedupeKey_WithNoRuleId_Throws() =>
        Assert.Throws<ArgumentException>(() => AlertTicketPolicy.DedupeKey(DeviceId, "  "));

    /// <summary>
    /// Severity sets urgency and impact, never the priority directly, so the automated ticket lands
    /// where <see cref="TicketPriorityMatrix"/> says it should rather than disagreeing with every
    /// ticket an agent raises.
    /// </summary>
    [Theory]
    [InlineData("Critical", TicketPriority.Critical)]
    [InlineData("Warning", TicketPriority.Medium)]
    public void Levels_ForASeverity_AgreeWithThePriorityMatrix(string severity, TicketPriority expected)
    {
        var (urgency, impact) = AlertTicketPolicy.Levels(severity);

        Assert.Equal(expected, TicketPriorityMatrix.Calculate(urgency, impact));
    }

    [Fact]
    public void Compose_ForACriticalAlert_CarriesTheFactsAndFitsTheColumn()
    {
        var draft = AlertTicketPolicy.Compose(
            Raised(severity: "Critical", summary: "Utilisation is above the critical threshold."),
            AlertCiContext.Unknown);

        Assert.StartsWith("[Critical] SNMP: CPU: Utilisation", draft.Title, StringComparison.Ordinal);
        Assert.True(draft.Title.Length <= 200);
        Assert.Contains("cpu.utilisation_percent", draft.Description, StringComparison.Ordinal);
        Assert.Contains("value 97.5", draft.Description, StringComparison.Ordinal);
        Assert.Contains("threshold 90", draft.Description, StringComparison.Ordinal);
        Assert.Contains("check:1:cpu.utilisation_percent", draft.Description, StringComparison.Ordinal);
        Assert.Equal(TicketLevel.High, draft.Urgency);
    }

    /// <summary>
    /// WP-3.5's summaries already open with the check's name, so prefixing it unconditionally gave
    /// "[Critical] SNMP: CPU: SNMP: CPU: …" on the live estate — a title an operator reads twice to
    /// parse. Found by hand-verification (2026-08-11), with both real shapes covered: a summary that
    /// repeats the name with a colon, and one that opens with it as a word.
    /// </summary>
    [Theory]
    [InlineData("SNMP: CPU", "SNMP: CPU: cpu.utilisation_percent is 91%.", "[Critical] SNMP: CPU: cpu")]
    [InlineData("Reachability", "Reachability on 192.0.2.1 is failing.", "[Critical] Reachability on 192")]
    public void Compose_WhenTheSummaryAlreadyOpensWithTheCheckName_DoesNotPrintItTwice(
        string checkName,
        string summary,
        string expectedPrefix)
    {
        var draft = AlertTicketPolicy.Compose(Raised(checkName: checkName, summary: summary), AlertCiContext.Unknown);

        Assert.StartsWith(expectedPrefix, draft.Title, StringComparison.Ordinal);
        Assert.DoesNotContain($"{checkName}: {checkName}", draft.Title, StringComparison.Ordinal);
    }

    /// <summary>
    /// The title column is 200 characters. A summary long enough to overflow it has to be cut here,
    /// because the alternative is a create that fails on a database constraint at 3 a.m.
    /// </summary>
    [Fact]
    public void Compose_WithAVeryLongSummary_TruncatesTheTitleRatherThanFailingTheInsert()
    {
        var draft = AlertTicketPolicy.Compose(Raised(summary: new string('x', 500)), AlertCiContext.Unknown);

        Assert.Equal(200, draft.Title.Length);
        Assert.EndsWith("…", draft.Title, StringComparison.Ordinal);
    }

    /// <summary>An availability rule has no threshold; the description must not invent one.</summary>
    [Fact]
    public void Compose_ForAnAvailabilityAlert_OmitsTheThreshold()
    {
        var draft = AlertTicketPolicy.Compose(
            Raised(summary: "The check has not completed for 3 cycles.", value: null, threshold: null),
            AlertCiContext.Unknown);

        Assert.DoesNotContain("threshold", draft.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Metric: cpu.utilisation_percent" + Environment.NewLine, draft.Description, StringComparison.Ordinal);
    }

    // ---- WP-3.7: the CMDB context the ticket carries ----

    /// <summary>
    /// The WP's first requirement, in the one place that decides what a ticket says: owner, location,
    /// warranty status and the open tickets already about this CI, all in the description.
    /// </summary>
    [Fact]
    public void Compose_WithCmdbContext_CarriesOwnerLocationWarrantyAndOpenRelatedTickets()
    {
        var draft = AlertTicketPolicy.Compose(Raised(), Context());

        Assert.Contains("Owner: Dana Whitfield", draft.Description, StringComparison.Ordinal);
        Assert.Contains("Location: Primary Data Centre", draft.Description, StringComparison.Ordinal);
        Assert.Contains("Department: Infrastructure", draft.Description, StringComparison.Ordinal);
        Assert.Contains("Asset tag: AST-0042", draft.Description, StringComparison.Ordinal);
        Assert.Contains("Warranty: ExpiringSoon — 12 day(s) left, expires 2026-08-23", draft.Description, StringComparison.Ordinal);
        Assert.Contains("Support contract: Dell ProSupport", draft.Description, StringComparison.Ordinal);
        Assert.Contains("Open related tickets: INC-000031 (InProgress, High)", draft.Description, StringComparison.Ordinal);
    }

    /// <summary>
    /// An expired warranty is the fact somebody actually acts on, so it says how long ago rather than
    /// printing a negative number of days left.
    /// </summary>
    [Fact]
    public void CmdbBlock_WithAnExpiredWarranty_SaysHowLongAgoItExpired()
    {
        var block = AlertTicketPolicy.CmdbBlock(
            CiId, Context(warrantyStatus: "Expired", expiresAt: new DateOnly(2026, 6, 1), daysRemaining: -71));

        Assert.Contains("Warranty: expired 71 day(s) ago on 2026-06-01", block, StringComparison.Ordinal);
        Assert.DoesNotContain("-71", block, StringComparison.Ordinal);
    }

    /// <summary>
    /// An unheld asset with no site is not the same as an asset whose CMDB record is missing. Both
    /// have to read as the fact they are — "Owner: —" on a CI nobody could find is a lie.
    /// </summary>
    [Fact]
    public void CmdbBlock_ForACiThatIsPresentButBare_NamesWhatIsMissingRatherThanLeavingItBlank()
    {
        var block = AlertTicketPolicy.CmdbBlock(
            CiId,
            new AlertCiContext(
                new CiSummary(CiId, "NetworkDevice", "core-sw-01", null, null, "Deployed", true, null, null),
                []));

        Assert.Contains("Owner: nobody holds this asset", block, StringComparison.Ordinal);
        Assert.Contains("Location: no site recorded", block, StringComparison.Ordinal);
        Assert.Contains("Warranty: no warranty date recorded", block, StringComparison.Ordinal);
        Assert.Contains("Open related tickets: none", block, StringComparison.Ordinal);
        Assert.DoesNotContain("Asset tag", block, StringComparison.Ordinal);
    }

    /// <summary>
    /// The failure path. Nothing stops a monitored device's CI being deleted (the WP-3.1 note about the
    /// missing port), and the alert is still worth a ticket — one that says the context could not be
    /// read rather than one that quietly implies the asset is unowned.
    /// </summary>
    [Fact]
    public void Compose_WhenTheCiIsNotInTheCmdb_StillProducesATicketAndSaysTheContextIsMissing()
    {
        var draft = AlertTicketPolicy.Compose(Raised(), AlertCiContext.Unknown);

        Assert.False(string.IsNullOrWhiteSpace(draft.Title));
        Assert.Contains("not found in the CMDB", draft.Description, StringComparison.Ordinal);
        Assert.DoesNotContain("Owner:", draft.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Compose_WithNoContext_Throws() =>
        Assert.Throws<ArgumentNullException>(() => AlertTicketPolicy.Compose(Raised(), null!));

    // ---- the root-cause ticket (WP-5.1) ----

    /// <summary>
    /// The half of "open ONE root-cause ticket listing affected CIs" that makes one ticket enough.
    /// Each of these has an alert of its own that was deliberately never published, so the ticket is
    /// the only place they are written down for whoever picks it up.
    /// </summary>
    [Fact]
    public void Compose_ForARootCauseAlert_NamesEveryCiTheOutageTookWithIt()
    {
        var draft = AlertTicketPolicy.Compose(Raised(), AlertCiContext.Unknown, [
            new ImpactedCi(Guid.CreateVersion7(), "dc1-esx-01", "Server", "Host is unreachable."),
            new ImpactedCi(Guid.CreateVersion7(), "dc1-app-01", "VirtualMachine", "Host is unreachable."),
        ]);

        Assert.Contains("Affected by this (2 CIs", draft.Description, StringComparison.Ordinal);
        Assert.Contains("- dc1-esx-01 (Server): Host is unreachable.", draft.Description, StringComparison.Ordinal);
        Assert.Contains("- dc1-app-01 (VirtualMachine)", draft.Description, StringComparison.Ordinal);
    }

    /// <summary>
    /// An ordinary alert explains nothing but itself, and its ticket must not gain a heading with
    /// nothing under it — "Affected: none" invites the reader to wonder what was meant to be there.
    /// </summary>
    [Fact]
    public void Compose_ForAnAlertThatExplainsNothing_SaysNothingAboutImpact()
    {
        var draft = AlertTicketPolicy.Compose(Raised(), AlertCiContext.Unknown);

        Assert.DoesNotContain("Affected by this", draft.Description, StringComparison.Ordinal);
    }

    /// <summary>
    /// A core switch can take a hundred CIs with it, and a description that is a hundred lines of
    /// inventory is one nobody reads to the end. The count is still the true one — a truncated list
    /// that under-reported the size of the outage would be worse than no list at all.
    /// </summary>
    [Fact]
    public void Compose_WithMoreAffectedCisThanFitInADescription_CountsTheRestRatherThanDroppingThem()
    {
        var impacted = Enumerable.Range(1, 25)
            .Select(index => new ImpactedCi(Guid.CreateVersion7(), $"host-{index:D2}", "Server", "Unreachable."))
            .ToArray();

        var draft = AlertTicketPolicy.Compose(Raised(), AlertCiContext.Unknown, impacted);

        Assert.Contains("Affected by this (25 CIs", draft.Description, StringComparison.Ordinal);
        Assert.Contains("host-20", draft.Description, StringComparison.Ordinal);
        Assert.DoesNotContain("host-21", draft.Description, StringComparison.Ordinal);
        Assert.Contains("…and 5 more", draft.Description, StringComparison.Ordinal);
    }

    /// <summary>
    /// A CI that has left the CMDB since its alert was raised is still listed, by id. Dropping it
    /// would make the ticket claim a smaller outage than the one that happened.
    /// </summary>
    [Fact]
    public void Compose_WithAnAffectedCiThatHasLeftTheCmdb_ListsItByIdRatherThanOmittingIt()
    {
        var missing = Guid.CreateVersion7();

        var draft = AlertTicketPolicy.Compose(Raised(), AlertCiContext.Unknown, [
            new ImpactedCi(missing, null, null, "Host is unreachable."),
        ]);

        Assert.Contains("Affected by this (1 CI,", draft.Description, StringComparison.Ordinal);
        Assert.Contains($"CI {missing} (no longer in the CMDB)", draft.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void RecurrenceNote_WhenTheSeverityRose_SaysSoRatherThanRepeatingItself()
    {
        var note = AlertTicketPolicy.RecurrenceNote(Raised(severity: "Critical"), 2, "Warning");

        Assert.Contains("escalated from Warning to Critical", note, StringComparison.Ordinal);
        Assert.Contains("Occurrence 2", note, StringComparison.Ordinal);
    }

    [Fact]
    public void RecurrenceNote_AtTheSameSeverity_ReadsAsARepeat()
    {
        var note = AlertTicketPolicy.RecurrenceNote(Raised(severity: "Warning"), 3, "Warning");

        Assert.Contains("raised again at Warning", note, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolutionNote_CarriesHowLongTheAlertWasOpen()
    {
        var note = AlertTicketPolicy.ResolutionNote(Cleared(durationSeconds: 3725));

        Assert.Contains("1h 2m", note, StringComparison.Ordinal);
        Assert.Contains("check:1:cpu.utilisation_percent", note, StringComparison.Ordinal);
    }

    [Fact]
    public void Compose_WithNoAlert_Throws() =>
        Assert.Throws<ArgumentNullException>(() => AlertTicketPolicy.Compose(null!, AlertCiContext.Unknown));

    private static readonly Guid CiId = Guid.Parse("6f9619ff-8b86-d011-b42d-00cf4fc964ff");

    private static AlertCiContext Context(
        string warrantyStatus = "ExpiringSoon",
        DateOnly? expiresAt = null,
        int? daysRemaining = 12) => new(
        new CiSummary(
            CiId,
            "NetworkDevice",
            "core-sw-01",
            "AST-0042",
            "SN-99",
            "Deployed",
            true,
            "Dana Whitfield",
            "Primary Data Centre",
            "Infrastructure",
            expiresAt ?? new DateOnly(2026, 8, 23),
            warrantyStatus,
            daysRemaining,
            "Dell ProSupport"),
        [
            new LinkedTicketSummary(
                Guid.CreateVersion7(), "INC-000031", "Switch keeps dropping ports", "InProgress", "High",
                DateTimeOffset.UtcNow.AddDays(-2)),
        ]);

    private static AlertRaised Raised(
        string severity = "Critical",
        string summary = "CPU utilisation 97.5% is above the critical threshold of 90%.",
        double? value = 97.5,
        double? threshold = 90,
        string checkName = "SNMP: CPU") => new(
        Guid.CreateVersion7(),
        DateTimeOffset.UtcNow,
        Guid.CreateVersion7(),
        DeviceId,
        Guid.CreateVersion7(),
        Guid.CreateVersion7(),
        "check:1:cpu.utilisation_percent",
        checkName,
        severity,
        "cpu.utilisation_percent",
        value,
        threshold,
        summary,
        DateTimeOffset.UtcNow,
        3);

    private static AlertCleared Cleared(long durationSeconds) => new(
        Guid.CreateVersion7(),
        DateTimeOffset.UtcNow,
        Guid.CreateVersion7(),
        DeviceId,
        Guid.CreateVersion7(),
        Guid.CreateVersion7(),
        "check:1:cpu.utilisation_percent",
        "SNMP: CPU",
        "Critical",
        "cpu.utilisation_percent",
        12.5,
        "CPU utilisation is back below the threshold.",
        DateTimeOffset.UtcNow.AddSeconds(-durationSeconds),
        durationSeconds);
}
