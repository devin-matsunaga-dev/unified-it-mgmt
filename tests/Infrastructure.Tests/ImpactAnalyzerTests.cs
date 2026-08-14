using Modules.Assets.Data;
using Modules.Assets.Features.Impact;

using Platform.Integration;

namespace Infrastructure.Tests;

/// <summary>
/// The blast-radius arithmetic, against a hand-written tree. No database and no clock — the SLA
/// exposure is handed in — so the whole of the WP's "matches graph fixture exactly, unit-tested against
/// known tree" lives here rather than behind an integration test that would prove the plumbing instead.
/// <para>
/// The fixture is one hypervisor host carrying two VMs, one of which carries a business service. It is
/// the shape of the seeded estate deliberately: <c>dc1-esx-01</c> → its VMs → the software on them →
/// the services that depend on it.
/// </para>
/// </summary>
public sealed class ImpactAnalyzerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);

    private static readonly Guid HostId = Guid.Parse("0198a000-0000-7000-8000-000000000001");
    private static readonly Guid VmAppId = Guid.Parse("0198a000-0000-7000-8000-000000000002");
    private static readonly Guid VmWebId = Guid.Parse("0198a000-0000-7000-8000-000000000003");
    private static readonly Guid ServiceId = Guid.Parse("0198a000-0000-7000-8000-000000000004");

    private static readonly Guid FinanceId = Guid.Parse("0198a000-0000-7000-8000-0000000000f1");
    private static readonly Guid ItId = Guid.Parse("0198a000-0000-7000-8000-0000000000f2");
    private static readonly Guid AlexId = Guid.Parse("0198a000-0000-7000-8000-0000000000a1");
    private static readonly Guid SamId = Guid.Parse("0198a000-0000-7000-8000-0000000000a2");

    /// <summary>The WP's own verification, in miniature: the host's VMs, their tickets, their departments.</summary>
    [Fact]
    public void Analyse_ForAHostCarryingVms_ReportsEveryOneOfThemAndTheRootItself()
    {
        var impact = ImpactAnalyzer.Analyse(Tree());

        Assert.Equal(HostId, impact.RootCiId);
        Assert.Equal(4, impact.Summary.CiCount);
        Assert.Equal(
            new[] { "DC1 hypervisor host 1", "Finance ERP application server", "Customer portal web front end 1", "Finance reporting service" }
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase),
            impact.Cis.Select(ci => ci.Name).OrderBy(name => name, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The root is part of its own outage and sits at depth 0; "direct" is the ring one hop out, which
    /// is what fails first and usually what is worth paging about.
    /// </summary>
    [Fact]
    public void Analyse_OrdersTheAffectedCisByDistanceFromTheFailure()
    {
        var impact = ImpactAnalyzer.Analyse(Tree());

        Assert.Equal([0, 1, 1, 2], impact.Cis.Select(ci => ci.Depth));
        Assert.Equal(HostId, impact.Cis[0].CiId);
        Assert.Equal(2, impact.Summary.DirectCiCount);
    }

    /// <summary>
    /// Within a depth the order is by name, so two runs of the same question answer in the same order —
    /// a panel whose rows move between refreshes reads as a panel that is changing its mind.
    /// </summary>
    [Fact]
    public void Analyse_WithinOneDepth_OrdersByNameSoTheAnswerIsStable()
    {
        var forwards = ImpactAnalyzer.Analyse(Tree());
        var backwards = ImpactAnalyzer.Analyse(Tree() with { Reached = [.. Tree().Reached.Reverse()] });

        Assert.Equal(forwards.Cis.Select(ci => ci.CiId), backwards.Cis.Select(ci => ci.CiId));
        Assert.Equal("Customer portal web front end 1", forwards.Cis[1].Name);
    }

    [Fact]
    public void Analyse_RollsTheAffectedCisUpByDepartmentAndByOwner()
    {
        var impact = ImpactAnalyzer.Analyse(Tree());

        Assert.Equal(2, impact.Summary.AffectedDepartmentCount);
        var finance = Assert.Single(impact.Departments, department => department.DepartmentId == FinanceId);
        Assert.Equal("Finance", finance.Name);
        Assert.Equal(2, finance.CiCount);

        Assert.Equal(2, impact.Summary.AffectedUserCount);
        var alex = Assert.Single(impact.Users, user => user.UserId == AlexId);
        Assert.Equal(2, alex.CiCount);
    }

    /// <summary>
    /// A CI nobody owns is counted and never bucketed under an invented department. A blast radius that
    /// makes up an owner is worse than one that admits it has none.
    /// </summary>
    [Fact]
    public void Analyse_WhenAnAffectedCiHasNoDepartment_CountsItWithoutInventingOne()
    {
        var impact = ImpactAnalyzer.Analyse(Tree());

        Assert.Equal(1, impact.Summary.CisWithoutDepartment);
        Assert.DoesNotContain(impact.Departments, department => department.Name.Contains("nassigned", StringComparison.Ordinal));
    }

    /// <summary>
    /// The tickets an outage already has behind it, worst exposure first: breached, then at risk, then
    /// by deadline. A ticket with no SLA sorts last — no deadline is not the most urgent deadline.
    /// </summary>
    [Fact]
    public void Analyse_OrdersTheOpenTicketsByHowExposedTheyAre()
    {
        var impact = ImpactAnalyzer.Analyse(Tree());

        Assert.Equal(["INC-000003", "INC-000002", "INC-000001", "INC-000004"], impact.Tickets.Select(ticket => ticket.Number));
        Assert.Equal(1, impact.Summary.BreachedSlaCount);
        Assert.Equal(1, impact.Summary.AtRiskSlaCount);
        Assert.Equal(Now.AddHours(2), impact.Summary.NextSlaDueAt);
    }

    /// <summary>
    /// A breached ticket has no deadline left to warn about, so the soonest deadline is the soonest one
    /// still to be met. Reporting the breached one would put a time in the past on a "next due" line.
    /// </summary>
    [Fact]
    public void Analyse_ForTheNextDeadline_IgnoresTheOnesAlreadyMissed()
    {
        var impact = ImpactAnalyzer.Analyse(Tree());

        Assert.NotNull(impact.Summary.NextSlaDueAt);
        Assert.True(impact.Summary.NextSlaDueAt > Now);
    }

    /// <summary>
    /// One ticket linked to two affected CIs is one piece of work. Counting it twice would inflate every
    /// number under it, and attributing it to the far end would file it under a CI it is not about.
    /// </summary>
    [Fact]
    public void Analyse_WhenOneTicketIsLinkedToTwoAffectedCis_CountsItOnceAgainstTheOneNearestTheFailure()
    {
        var subject = Tree() with
        {
            Tickets =
            [
                Ticket("INC-000009", VmAppId, priority: "High", sla: null),
                Ticket("INC-000009", ServiceId, priority: "High", sla: null),
            ],
            TicketTotal = 1,
        };

        var impact = ImpactAnalyzer.Analyse(subject);

        var ticket = Assert.Single(impact.Tickets);
        Assert.Equal(VmAppId, ticket.CiId);
        Assert.Equal(1, impact.Summary.OpenTicketCount);
        Assert.Equal(1, Assert.Single(impact.Cis, ci => ci.CiId == VmAppId).OpenTicketCount);
    }

    /// <summary>
    /// A ticket against a CI outside the radius is not this outage's, however it reached the analyzer.
    /// The failure path: the directory is asked about a scope, and anything else it answers is dropped
    /// rather than attributed to a CI the response does not contain — which would throw on the lookup.
    /// </summary>
    [Fact]
    public void Analyse_WhenATicketNamesACiOutsideTheRadius_LeavesItOut()
    {
        var stranger = Guid.Parse("0198a000-0000-7000-8000-0000000000ff");
        var subject = Tree() with
        {
            Tickets = [Ticket("INC-000009", stranger, priority: "Critical", sla: Breached())],
            TicketTotal = 1,
        };

        var impact = ImpactAnalyzer.Analyse(subject);

        Assert.Empty(impact.Tickets);
        Assert.Equal(0, impact.Summary.BreachedSlaCount);
    }

    /// <summary>
    /// The total comes from the directory rather than from the length of the list, so a capped panel
    /// still states the honest number. WP-2.4's rule: a truncated answer must never look complete.
    /// </summary>
    [Fact]
    public void Analyse_WhenMoreTicketsAreOpenThanWereReturned_KeepsTheTrueTotalAndSaysItIsTruncated()
    {
        var impact = ImpactAnalyzer.Analyse(Tree() with { TicketTotal = 97 });

        Assert.Equal(97, impact.Summary.OpenTicketCount);
        Assert.True(impact.Summary.TicketsTruncated);
        Assert.Equal(4, impact.Tickets.Count);
    }

    [Fact]
    public void Analyse_WhenNothingIsTruncated_SaysSo()
    {
        var impact = ImpactAnalyzer.Analyse(Tree());

        Assert.False(impact.Summary.TicketsTruncated);
        Assert.False(impact.Summary.CisTruncated);
    }

    /// <summary>
    /// Nothing depends on this CI, so the blast radius is the CI itself. Zero would be wrong: taking it
    /// away still takes it away.
    /// </summary>
    [Fact]
    public void Analyse_ForACiNothingDependsOn_IsTheCiItselfAndNothingElse()
    {
        var impact = ImpactAnalyzer.Analyse(new ImpactSubject(
            Ci(HostId, "DC1 hypervisor host 1", CiType.Server, 0, ItId, "IT", AlexId, "Alex Doe"),
            [],
            [],
            0,
            MaxDepth: 5,
            MaxDepthReached: false,
            ContainsCycle: false));

        var only = Assert.Single(impact.Cis);
        Assert.Equal(HostId, only.CiId);
        Assert.Equal(1, impact.Summary.CiCount);
        Assert.Equal(0, impact.Summary.DirectCiCount);
        Assert.Equal(0, impact.Summary.OpenTicketCount);
        Assert.Null(impact.Summary.NextSlaDueAt);
    }

    /// <summary>
    /// A cycle among the affected CIs is a real estate shape (WP-2.3 accepts clustered pairs), and the
    /// walk already refuses to re-enter one. Each CI is still counted once; the flag is how a reader
    /// comparing this against the relationship tree learns why it is not a tree.
    /// </summary>
    [Fact]
    public void Analyse_WhenTheWalkReturnsACiTwice_CountsItOnce()
    {
        var subject = Tree();
        var impact = ImpactAnalyzer.Analyse(subject with
        {
            Reached = [.. subject.Reached, subject.Reached[0] with { Depth = 3 }],
            ContainsCycle = true,
        });

        Assert.Equal(4, impact.Summary.CiCount);
        Assert.True(impact.ContainsCycle);
        Assert.Single(impact.Cis, ci => ci.CiId == subject.Reached[0].CiId);
    }

    /// <summary>
    /// One host, two VMs on it, one business service behind one of them — and four open tickets whose
    /// SLA states cover every case the summary counts.
    /// </summary>
    private static ImpactSubject Tree() => new(
        Ci(HostId, "DC1 hypervisor host 1", CiType.Server, 0, ItId, "IT", AlexId, "Alex Doe"),
        [
            Ci(VmAppId, "Finance ERP application server", CiType.Virtual, 1, FinanceId, "Finance", AlexId, "Alex Doe"),
            Ci(VmWebId, "Customer portal web front end 1", CiType.Virtual, 1, null, null, SamId, "Sam Roe"),
            Ci(ServiceId, "Finance reporting service", CiType.Logical, 2, FinanceId, "Finance", SamId, "Sam Roe"),
        ],
        [
            Ticket("INC-000001", VmAppId, "Medium", Exposure(Now.AddHours(2))),
            Ticket("INC-000002", ServiceId, "High", AtRisk()),
            Ticket("INC-000003", VmWebId, "Critical", Breached()),
            Ticket("INC-000004", VmAppId, "Low", sla: null),
        ],
        TicketTotal: 4,
        MaxDepth: 5,
        MaxDepthReached: false,
        ContainsCycle: false);

    private static ImpactCi Ci(
        Guid id, string name, CiType type, int depth,
        Guid? departmentId, string? departmentName, Guid? ownerId, string? ownerName) =>
        new(id, name, type, CiLifecycleState.Deployed, true, depth,
            ownerId, ownerName, departmentId, departmentName, "Primary Data Centre");

    private static ImpactedTicketSummary Ticket(string number, Guid ciId, string priority, SlaExposure? sla) =>
        new(
            Guid.Parse($"0198a000-0000-7000-8000-0000000{number[^5..]}"),
            ciId,
            number,
            $"Something is wrong ({number})",
            "In Progress",
            priority,
            Now.AddHours(-3),
            sla);

    private static SlaExposure Exposure(DateTimeOffset dueAt) =>
        new("Standard", dueAt, (dueAt - Now).TotalSeconds, Breached: false, AtRisk: false);

    private static SlaExposure AtRisk() =>
        new("Standard", Now.AddHours(6), TimeSpan.FromHours(6).TotalSeconds, Breached: false, AtRisk: true);

    private static SlaExposure Breached() =>
        new("Standard", Now, 0, Breached: true, AtRisk: false);
}
