using Platform.Integration;

namespace Modules.Monitoring.Features.Alerting;

/// <summary>A CI that has something wrong with it, and when the oldest of those things started.</summary>
/// <param name="FailingSince">
/// The <c>RaisedAt</c> of its oldest open alert. Not the newest: a switch whose availability rule went
/// first and whose interface rules followed is one failure that grew, and the moment it began is what
/// the window is measured from.
/// </param>
public sealed record FailingCi(Guid CiId, DateTimeOffset FailingSince);

/// <summary>"This CI's trouble is explained by that one, which is <paramref name="Depth"/> hops away."</summary>
public sealed record AlertCorrelation(Guid CiId, Guid RootCauseCiId, int Depth);

/// <summary>
/// The core of WP-5.1, and the only place a set of failures becomes a cause and a set of consequences.
/// Pure: no clock, no database, no graph query — it is handed the CIs that are failing and the
/// dependency edges among them, and it answers which of them explain the others. That is what makes
/// the whole of "root-cause suppression" testable by calling a function, the same split WP-3.5 made
/// between <see cref="AlertEngine"/> and <see cref="AlertStateMachine"/>.
/// <para>
/// The safety property it is built around, and the one worth breaking a test over: <em>an alert is
/// never suppressed unless some other alert that is going to be published explains it.</em> Every
/// branch below that cannot name a root leaves the CI alone, so the failure mode of this code is a
/// duplicate ticket and never a silent outage.
/// </para>
/// </summary>
public static class AlertCorrelator
{
    /// <summary>
    /// Which of <paramref name="failing"/> are consequences of others. CIs absent from the result are
    /// causes in their own right — including every CI on an estate where nothing depends on anything,
    /// which is why this returns the impacted rather than a verdict per CI.
    /// </summary>
    /// <param name="dependencies">
    /// Transitive: <see cref="ICiDependencyDirectory"/> reports every failing ancestor rather than only
    /// the adjacent one, so a chain of three is two edges to the root and not a walk this has to make.
    /// </param>
    /// <param name="window">
    /// How far apart two failures may have begun and still be one incident. A dependent that stayed up
    /// for an hour after its dependency died was not killed by it — whatever has just happened to it is
    /// news, and burying it under an hour-old ticket is how a real second outage goes unnoticed.
    /// Measured in both directions, because a poller can report the consequence a cycle before the
    /// cause depending on which device it got to first.
    /// </param>
    public static IReadOnlyList<AlertCorrelation> Correlate(
        IReadOnlyCollection<FailingCi> failing,
        IReadOnlyCollection<CiDependencyLink> dependencies,
        TimeSpan window)
    {
        ArgumentNullException.ThrowIfNull(failing);
        ArgumentNullException.ThrowIfNull(dependencies);

        if (failing.Count < 2 || dependencies.Count == 0)
        {
            return [];
        }

        var failingSince = new Dictionary<Guid, DateTimeOffset>();
        foreach (var candidate in failing)
        {
            // Defensive against a caller that names one CI twice: keep the earlier start, which is the
            // same rule the caller itself applies across a CI's several alerts.
            if (!failingSince.TryGetValue(candidate.CiId, out var existing) || candidate.FailingSince < existing)
            {
                failingSince[candidate.CiId] = candidate.FailingSince;
            }
        }

        var eligible = dependencies
            .Where(link => IsEligible(link, failingSince, window))
            .ToList();
        if (eligible.Count == 0)
        {
            return [];
        }

        // A root is a failure nothing else in this set explains. Computed from the eligible edges only:
        // an out-of-window dependency is not an explanation, so a CI whose only failing dependency died
        // an hour ago is a root and gets its own ticket, which is the point of the window.
        var explained = eligible.Select(link => link.CiId).ToHashSet();
        var roots = failingSince.Keys.Where(ci => !explained.Contains(ci)).ToHashSet();

        var correlations = new List<AlertCorrelation>();
        foreach (var link in eligible.GroupBy(link => link.CiId))
        {
            // Only a root may be named as a cause. A dependency that is itself a consequence is not the
            // thing to open a ticket about, and naming one would put an operator one hop from the
            // answer instead of on it.
            //
            // This is also what makes a cycle safe. Two mutually dependent CIs failing together explain
            // each other, so neither is a root, so neither is in `roots` and neither is suppressed —
            // both publish, both get a ticket, and nobody is left with an outage nothing reported.
            var candidates = link
                .Where(edge => roots.Contains(edge.DependsOnCiId))
                .ToList();
            if (candidates.Count == 0)
            {
                continue;
            }

            // Deepest first: the far end of the chain is the thing that actually broke. Ties are broken
            // on the id so that the answer never depends on the order rows came back from the database
            // — WP-4.4's rule for normalisation precedence, and it matters more here, because a tie
            // resolved differently on two consecutive cycles would move an alert between two tickets.
            var cause = candidates
                .OrderByDescending(edge => edge.Depth)
                .ThenBy(edge => edge.DependsOnCiId)
                .First();
            correlations.Add(new AlertCorrelation(link.Key, cause.DependsOnCiId, cause.Depth));
        }

        return correlations;
    }

    private static bool IsEligible(
        CiDependencyLink link,
        IReadOnlyDictionary<Guid, DateTimeOffset> failingSince,
        TimeSpan window)
    {
        // Both ends have to be failing. The port only ever reports pairs from the set it was handed, so
        // this is a guard against a caller narrowing the set after asking rather than a real case.
        if (!failingSince.TryGetValue(link.CiId, out var dependent)
            || !failingSince.TryGetValue(link.DependsOnCiId, out var dependency))
        {
            return false;
        }

        // A CI never explains itself. The traversal excludes the root it started from, so this only
        // fires on a self-relationship, which WP-2.3 refuses at the API — belt and braces, because the
        // consequence of getting it wrong is an alert suppressed underneath itself and told to nobody.
        return link.CiId != link.DependsOnCiId
            && (dependent - dependency).Duration() <= window;
    }
}
