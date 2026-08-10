namespace Modules.Helpdesk.Features.AlertTickets;

/// <summary>
/// The shortest legal route between two ticket statuses, read out of the WP-1.2 transition graph
/// rather than hardcoded.
/// <para>
/// It exists because "auto-resolve" cannot be one move: the seeded graph is the linear chain
/// New → Triage → InProgress → Pending → Resolved → Closed, so a ticket sitting at New has four
/// transitions to make before it is resolved. Adding a shortcut edge would change the workflow for
/// every human agent too, which is a different work package's decision — so the automation walks the
/// path the graph already permits, and each hop goes through <c>ITicketService.TransitionAsync</c> so
/// the guard, the history row, the SLA clock and the audit entry all still happen.
/// </para>
/// <para>
/// Breadth-first rather than "the next status by display order", so a graph that later gains a
/// shortcut is used rather than ignored, and one that loses an edge fails to find a path instead of
/// walking into a 409.
/// </para>
/// </summary>
public static class TicketStatusPath
{
    /// <summary>
    /// The statuses to move through, excluding <paramref name="from"/> and including
    /// <paramref name="to"/>. Empty when they are the same status; null when no route exists.
    /// </summary>
    public static IReadOnlyList<Guid>? Find(
        IReadOnlyCollection<(Guid From, Guid To)> edges,
        Guid from,
        Guid to)
    {
        ArgumentNullException.ThrowIfNull(edges);

        if (from == to)
        {
            return [];
        }

        var outgoing = edges.GroupBy(edge => edge.From)
            .ToDictionary(group => group.Key, group => group.Select(edge => edge.To).ToList());
        var cameFrom = new Dictionary<Guid, Guid>();
        var seen = new HashSet<Guid> { from };
        var queue = new Queue<Guid>();
        queue.Enqueue(from);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var next in outgoing.GetValueOrDefault(current) ?? [])
            {
                if (!seen.Add(next))
                {
                    continue;
                }

                cameFrom[next] = current;
                if (next == to)
                {
                    return Rebuild(cameFrom, from, to);
                }

                queue.Enqueue(next);
            }
        }

        return null;
    }

    private static List<Guid> Rebuild(Dictionary<Guid, Guid> cameFrom, Guid from, Guid to)
    {
        var path = new List<Guid>();
        var current = to;
        while (current != from)
        {
            path.Add(current);
            current = cameFrom[current];
        }

        path.Reverse();
        return path;
    }
}
