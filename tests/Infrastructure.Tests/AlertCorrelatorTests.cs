using Modules.Monitoring.Features.Alerting;

using Platform.Integration;

namespace Infrastructure.Tests;

/// <summary>
/// The WP's verification list, driven through the correlator itself: an ancestor that is down makes
/// its descendants consequences, a leaf failing on its own is untouched, and nothing that cannot name
/// a cause is ever silenced.
/// <para>
/// No database, no graph query, no clock — every one of these is a call to a pure function over a
/// hand-written graph, which is the whole reason the decisions live where they do.
/// </para>
/// </summary>
public sealed class AlertCorrelatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 14, 9, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    // Fixed rather than minted per run, so a tie-break assertion is about the rule and not about which
    // GUID this run happened to allocate. Named for their place in the tree.
    private static readonly Guid Switch = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Host = new("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Vm = new("33333333-3333-3333-3333-333333333333");
    private static readonly Guid Unrelated = new("44444444-4444-4444-4444-444444444444");
    private static readonly Guid OtherSwitch = new("55555555-5555-5555-5555-555555555555");

    // ---- the WP's demo ----

    /// <summary>
    /// "Stop sim core switch (with 5 dependents down) → exactly 1 ticket, 5 suppressed alerts."
    /// Five dependents, one cause, and the cause is not itself a consequence of anything.
    /// </summary>
    [Fact]
    public void Correlate_WhenACoreSwitchAndItsDependentsFail_ExplainsEveryDependentByTheSwitch()
    {
        var dependents = Enumerable.Range(1, 5)
            .Select(index => Guid.Parse($"aaaaaaaa-0000-0000-0000-00000000000{index}"))
            .ToArray();
        var failing = new[] { Failing(Switch) }
            .Concat(dependents.Select(ci => Failing(ci, Now.AddSeconds(30))))
            .ToArray();
        var links = dependents.Select(ci => new CiDependencyLink(ci, Switch, 1)).ToArray();

        var result = AlertCorrelator.Correlate(failing, links, Window);

        Assert.Equal(5, result.Count);
        Assert.All(result, correlation => Assert.Equal(Switch, correlation.RootCauseCiId));
        Assert.DoesNotContain(result, correlation => correlation.CiId == Switch);
    }

    /// <summary>
    /// "Stop a leaf only → normal single alert path unaffected." One failure explains nothing, and the
    /// correlator does not even reach for the graph.
    /// </summary>
    [Fact]
    public void Correlate_WhenOnlyOneCiIsFailing_ExplainsNothing()
    {
        var result = AlertCorrelator.Correlate(
            [Failing(Vm)],
            [new CiDependencyLink(Vm, Host, 1)],
            Window);

        Assert.Empty(result);
    }

    /// <summary>
    /// Two things broken at once with no dependency between them are two incidents, and both are
    /// reported. This is the case that would make correlation dangerous if it guessed.
    /// </summary>
    [Fact]
    public void Correlate_WhenTwoUnrelatedCisFail_ExplainsNeither()
    {
        var result = AlertCorrelator.Correlate([Failing(Switch), Failing(Unrelated)], [], Window);

        Assert.Empty(result);
    }

    // ---- chains ----

    /// <summary>
    /// A VM on a host on a switch: both the host and the VM are filed under the switch, not under each
    /// other. Naming the host as the VM's cause would leave an operator one hop from the answer.
    /// </summary>
    [Fact]
    public void Correlate_AlongAChain_FilesEveryLinkUnderTheFarEnd()
    {
        var result = AlertCorrelator.Correlate(
            [Failing(Switch), Failing(Host), Failing(Vm)],
            [
                new CiDependencyLink(Host, Switch, 1),
                new CiDependencyLink(Vm, Host, 1),
                new CiDependencyLink(Vm, Switch, 2),
            ],
            Window);

        Assert.Equal(2, result.Count);
        Assert.All(result, correlation => Assert.Equal(Switch, correlation.RootCauseCiId));
    }

    /// <summary>
    /// The dependency is healthy, so it is not in the failing set and nothing explains the VM. A
    /// correlation is only ever drawn between two things that are both broken.
    /// </summary>
    [Fact]
    public void Correlate_WhenTheDependencyIsHealthy_ExplainsNothing()
    {
        var result = AlertCorrelator.Correlate(
            [Failing(Vm), Failing(Unrelated)],
            [new CiDependencyLink(Vm, Host, 1)],
            Window);

        Assert.Empty(result);
    }

    // ---- the time window ----

    /// <summary>
    /// A dependent that stayed up for an hour after its dependency died was not killed by it. Filing
    /// it under an hour-old ticket is how a genuinely new outage goes unnoticed.
    /// </summary>
    [Fact]
    public void Correlate_WhenTheFailuresAreFarApart_ExplainsNothing()
    {
        var result = AlertCorrelator.Correlate(
            [Failing(Switch, Now), Failing(Host, Now.AddHours(1))],
            [new CiDependencyLink(Host, Switch, 1)],
            Window);

        Assert.Empty(result);
    }

    /// <summary>
    /// A poller can reach the consequence before the cause, so the window is measured in both
    /// directions. A dependent reported thirty seconds early is the same incident.
    /// </summary>
    [Fact]
    public void Correlate_WhenTheConsequenceIsReportedFirst_StillExplainsIt()
    {
        var result = AlertCorrelator.Correlate(
            [Failing(Switch, Now), Failing(Host, Now.AddSeconds(-30))],
            [new CiDependencyLink(Host, Switch, 1)],
            Window);

        var correlation = Assert.Single(result);
        Assert.Equal(Host, correlation.CiId);
        Assert.Equal(Switch, correlation.RootCauseCiId);
    }

    // ---- determinism ----

    /// <summary>
    /// Two causes at the same depth are broken on the id rather than on whatever order the database
    /// returned. A tie resolved differently on two consecutive cycles would move a suppressed alert
    /// between two tickets.
    /// </summary>
    [Fact]
    public void Correlate_WithTwoEquallyDeepCauses_PicksTheSameOneWhicheverOrderTheyArriveIn()
    {
        var links = new[]
        {
            new CiDependencyLink(Vm, Switch, 1),
            new CiDependencyLink(Vm, OtherSwitch, 1),
        };
        var failing = new[] { Failing(Vm), Failing(Switch), Failing(OtherSwitch) };

        var forwards = AlertCorrelator.Correlate(failing, links, Window);
        var backwards = AlertCorrelator.Correlate([.. failing.Reverse()], [.. links.Reverse()], Window);

        Assert.Equal(Switch, Assert.Single(forwards).RootCauseCiId);
        Assert.Equal(Switch, Assert.Single(backwards).RootCauseCiId);
    }

    /// <summary>The deeper cause wins, because the far end of a chain is the thing that actually broke.</summary>
    [Fact]
    public void Correlate_WithCausesAtDifferentDepths_PicksTheDeeperOne()
    {
        var result = AlertCorrelator.Correlate(
            [Failing(Vm), Failing(Switch)],
            [
                // Both are roots — neither depends on anything else that is failing — so the choice is
                // made on distance alone.
                new CiDependencyLink(Vm, OtherSwitch, 1),
                new CiDependencyLink(Vm, Switch, 3),
            ],
            Window);

        Assert.Equal(Switch, Assert.Single(result).RootCauseCiId);
    }

    // ---- failure paths ----

    /// <summary>
    /// The safety property, and the reason this class exists: <em>nothing is suppressed unless
    /// something else is going to be published to explain it.</em> Two mutually dependent CIs — a
    /// clustered pair, a redundant link, both real estate shapes WP-2.3 deliberately allows — explain
    /// each other, so neither is a root, so neither is silenced. Two tickets is the correct answer
    /// here; nought tickets would be an outage nobody was told about.
    /// </summary>
    [Fact]
    public void Correlate_WhenTwoFailingCisDependOnEachOther_SuppressesNeither()
    {
        var result = AlertCorrelator.Correlate(
            [Failing(Switch), Failing(OtherSwitch)],
            [
                new CiDependencyLink(Switch, OtherSwitch, 1),
                new CiDependencyLink(OtherSwitch, Switch, 1),
            ],
            Window);

        Assert.Empty(result);
    }

    /// <summary>
    /// The same property one hop further out: a CI hanging off a cycle has no root to be filed under,
    /// so it speaks for itself rather than being filed under a cause that is itself a consequence.
    /// </summary>
    [Fact]
    public void Correlate_WhenTheOnlyCauseIsInsideACycle_SuppressesNothing()
    {
        var result = AlertCorrelator.Correlate(
            [Failing(Switch), Failing(OtherSwitch), Failing(Host)],
            [
                new CiDependencyLink(Switch, OtherSwitch, 1),
                new CiDependencyLink(OtherSwitch, Switch, 1),
                new CiDependencyLink(Host, Switch, 1),
            ],
            Window);

        Assert.Empty(result);
    }

    /// <summary>
    /// A self-dependency cannot arise through the API (WP-2.3 refuses one) and the traversal excludes
    /// the root it started from — but an alert suppressed underneath itself would be told to nobody at
    /// all, so it is refused here as well.
    /// </summary>
    [Fact]
    public void Correlate_WithACiThatDependsOnItself_IgnoresTheEdge()
    {
        var result = AlertCorrelator.Correlate(
            [Failing(Switch), Failing(Host)],
            [new CiDependencyLink(Switch, Switch, 1)],
            Window);

        Assert.Empty(result);
    }

    [Fact]
    public void Correlate_WithNoFailures_Throws() =>
        Assert.Throws<ArgumentNullException>(() => AlertCorrelator.Correlate(null!, [], Window));

    [Fact]
    public void Correlate_WithNoDependencies_Throws() =>
        Assert.Throws<ArgumentNullException>(() => AlertCorrelator.Correlate([], null!, Window));

    private static FailingCi Failing(Guid ciId, DateTimeOffset? since = null) =>
        new(ciId, since ?? Now);
}
