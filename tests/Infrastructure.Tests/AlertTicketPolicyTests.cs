using Contracts.Events;

using Modules.Helpdesk.Data;
using Modules.Helpdesk.Features.AlertTickets;
using Modules.Helpdesk.Features.Tickets;

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
            Raised(severity: "Critical", summary: "Utilisation is above the critical threshold."));

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
        var draft = AlertTicketPolicy.Compose(Raised(checkName: checkName, summary: summary));

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
        var draft = AlertTicketPolicy.Compose(Raised(summary: new string('x', 500)));

        Assert.Equal(200, draft.Title.Length);
        Assert.EndsWith("…", draft.Title, StringComparison.Ordinal);
    }

    /// <summary>An availability rule has no threshold; the description must not invent one.</summary>
    [Fact]
    public void Compose_ForAnAvailabilityAlert_OmitsTheThreshold()
    {
        var draft = AlertTicketPolicy.Compose(
            Raised(summary: "The check has not completed for 3 cycles.", value: null, threshold: null));

        Assert.DoesNotContain("threshold", draft.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Metric: cpu.utilisation_percent" + Environment.NewLine, draft.Description, StringComparison.Ordinal);
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
        Assert.Throws<ArgumentNullException>(() => AlertTicketPolicy.Compose(null!));

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
