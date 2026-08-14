namespace Modules.Assets.Features.Impact;

/// <summary>
/// Turns a walked graph, the open tickets on it and the SLA clocks behind them into the answer to "what
/// breaks if this dies". Pure — no database, no clock, no configuration — so the whole of it can be
/// asserted against a hand-written tree, which is what the WP text asks for.
/// </summary>
public static class ImpactAnalyzer
{
    /// <summary>The most affected CIs a response will carry. Beyond this the answer is "the estate".</summary>
    public const int MaximumCis = 200;

    /// <summary>The most tickets a response will carry, matching what the panel can usefully render.</summary>
    public const int MaximumTickets = 50;

    public static ImpactResponse Analyse(ImpactSubject subject)
    {
        ArgumentNullException.ThrowIfNull(subject);

        // The root is part of its own outage and sits at depth 0. A walk that somehow returned it as
        // well — a cycle leading back round — must not make it two CIs.
        var affected = subject.Reached
            .Where(ci => ci.CiId != subject.Root.CiId)
            .Prepend(subject.Root)
            .DistinctBy(ci => ci.CiId)
            .OrderBy(ci => ci.Depth)
            .ThenBy(ci => ci.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(ci => ci.CiId)
            .ToList();

        var byId = affected.ToDictionary(ci => ci.CiId);

        // A ticket linked to two affected CIs is one piece of work, attributed to whichever of them is
        // nearest the failure. Counting it twice would inflate every number below it, and picking the
        // far end would file a ticket under a CI that is only affected *through* the one it is really
        // about.
        var tickets = subject.Tickets
            .Where(ticket => byId.ContainsKey(ticket.CiId))
            .GroupBy(ticket => ticket.TicketId)
            .Select(group => group
                .OrderBy(ticket => byId[ticket.CiId].Depth)
                .ThenBy(ticket => byId[ticket.CiId].Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(ticket => ticket.CiId)
                .First())
            .OrderByDescending(ticket => ticket.Sla?.Breached ?? false)
            .ThenByDescending(ticket => ticket.Sla?.AtRisk ?? false)
            .ThenBy(ticket => ticket.Sla?.ResolutionDueAt ?? DateTimeOffset.MaxValue)
            .ThenBy(ticket => ticket.CreatedAt)
            .ThenBy(ticket => ticket.TicketId)
            .ToList();

        var ticketsByCi = tickets
            .GroupBy(ticket => ticket.CiId)
            .ToDictionary(group => group.Key, group => group.Count());

        var departments = affected
            .Where(ci => ci.DepartmentId is not null)
            .GroupBy(ci => ci.DepartmentId!.Value)
            .Select(group => new ImpactedDepartmentResponse(
                group.Key,
                // The name is snapshotted per CI, so two CIs could disagree after a rename. The first by
                // the ordering above wins rather than an arbitrary one, and the CMDB is where a stale
                // snapshot gets corrected.
                group.Select(ci => ci.DepartmentName).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name))
                    ?? "Unnamed department",
                group.Count(),
                group.Sum(ci => ticketsByCi.GetValueOrDefault(ci.CiId))))
            .OrderByDescending(department => department.OpenTicketCount)
            .ThenByDescending(department => department.CiCount)
            .ThenBy(department => department.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(department => department.DepartmentId)
            .ToList();

        var users = affected
            .Where(ci => ci.OwnerUserId is not null)
            .GroupBy(ci => ci.OwnerUserId!.Value)
            .Select(group => new ImpactedUserResponse(
                group.Key,
                group.Select(ci => ci.OwnerName).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name))
                    ?? "Unnamed user",
                group.Count(),
                group.Sum(ci => ticketsByCi.GetValueOrDefault(ci.CiId))))
            .OrderByDescending(user => user.OpenTicketCount)
            .ThenByDescending(user => user.CiCount)
            .ThenBy(user => user.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(user => user.UserId)
            .ToList();

        var exposed = tickets.Where(ticket => ticket.Sla is not null).ToList();
        var summary = new ImpactSummaryResponse(
            affected.Count,
            affected.Count(ci => ci.Depth == 1),
            // The directory's own total, not the length of the list: it counts every open ticket on the
            // radius, including the ones the cap left out. Never below what was actually returned, so a
            // caller that supplied a total and a longer list cannot make this claim less than it can show.
            Math.Max(subject.TicketTotal, tickets.Count),
            exposed.Count(ticket => ticket.Sla!.Breached),
            exposed.Count(ticket => ticket.Sla!.AtRisk),
            // The soonest deadline still to be met. A breached ticket has no deadline left to warn
            // about, so it is excluded here and counted above instead.
            exposed
                .Where(ticket => !ticket.Sla!.Breached)
                .Select(ticket => (DateTimeOffset?)ticket.Sla!.ResolutionDueAt)
                .Order()
                .FirstOrDefault(),
            users.Count,
            departments.Count,
            affected.Count(ci => ci.DepartmentId is null),
            CisTruncated: affected.Count > MaximumCis,
            TicketsTruncated: tickets.Count > MaximumTickets || subject.TicketTotal > tickets.Count);

        return new ImpactResponse(
            subject.Root.CiId,
            subject.Root.Name,
            subject.Root.Type,
            subject.MaxDepth,
            subject.MaxDepthReached,
            subject.ContainsCycle,
            summary,
            [
                .. affected
                    .Take(MaximumCis)
                    .Select(ci => new ImpactedCiResponse(
                        ci.CiId,
                        ci.Name,
                        ci.Type,
                        ci.LifecycleState,
                        ci.IsActive,
                        ci.Depth,
                        ci.OwnerUserId,
                        ci.OwnerName,
                        ci.DepartmentId,
                        ci.DepartmentName,
                        ci.SiteName,
                        ticketsByCi.GetValueOrDefault(ci.CiId))),
            ],
            [
                .. tickets
                    .Take(MaximumTickets)
                    .Select(ticket => new ImpactedTicketResponse(
                        ticket.TicketId,
                        ticket.Number,
                        ticket.Title,
                        ticket.Status,
                        ticket.Priority,
                        ticket.CreatedAt,
                        ticket.CiId,
                        byId[ticket.CiId].Name,
                        ticket.Sla)),
            ],
            departments,
            users);
    }
}
