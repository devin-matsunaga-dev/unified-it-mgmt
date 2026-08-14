using Microsoft.EntityFrameworkCore;

using Modules.Assets.Data;

using Platform.Auditing;
using Platform.Integration;

namespace Modules.Assets.Features.Timeline;

/// <summary>
/// Gathers one CI's history from the four places it is kept and hands it to
/// <see cref="CiTimelineAssembler"/> (WP-5.3).
/// <para>
/// The service lives in Assets for the reason WP-5.2 put the blast radius here: the CI is the subject, and
/// the lifecycle and the audit trail are already within reach. What it has to cross a boundary for is the
/// alerts and the tickets — two ports, which is the design decision this package turns on, and neither of
/// them is a write.
/// </para>
/// <para>
/// A source the filter excluded is never queried. That is what makes "alerts only" cheaper than the whole
/// timeline rather than the same read with rows thrown away afterwards.
/// </para>
/// </summary>
public sealed class CiTimelineService(
    AssetsDbContext dbContext,
    ICiAlertHistoryDirectory alertHistory,
    ICiTicketHistoryDirectory ticketHistory,
    IAuditTrail auditTrail) : ICiTimelineService
{
    /// <summary>The default per-source cap, and the ceiling a caller may raise it to.</summary>
    public const int DefaultLimit = 50;

    public const int MaximumLimit = 200;

    /// <summary>
    /// Audited actions the timeline reads from a better source instead.
    /// <para>
    /// A lifecycle move writes both an <c>assets.ci_lifecycle_history</c> row — with its from-state, its
    /// to-state and the note whoever moved it typed — and an audit row holding the whole CI before and
    /// after. Both are true; only one is worth reading. Without this the axis would carry "Deployed → In
    /// repair" and, on the same second, "Record updated. Changed lifecycleState, updatedAt."
    /// </para>
    /// </summary>
    private static readonly string[] LifecycleActions = ["LifecycleChanged", "AssignmentChanged"];

    public async Task<CiTimelineResponse?> GetTimelineAsync(
        Guid ciId,
        CiTimelineRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var ci = await dbContext.Cis
            .AsNoTracking()
            .Where(entity => entity.Id == ciId)
            .Select(entity => new { entity.Id, entity.Name })
            .FirstOrDefaultAsync(cancellationToken);
        if (ci is null)
        {
            return null;
        }

        var limit = Math.Clamp(request.Limit, 1, MaximumLimit);
        var kinds = request.Kinds.Count == 0 ? CiTimelineAssembler.AllKinds : request.Kinds;

        var alerts = kinds.Contains(CiTimelineEventKind.Alert)
            ? await alertHistory.GetAlertsForCiAsync(ciId, request.From, request.To, limit, cancellationToken)
            : new CiAlertHistory([], 0);

        var tickets = kinds.Contains(CiTimelineEventKind.Ticket)
            ? await ticketHistory.GetTicketsForCiAsync(ciId, request.From, request.To, limit, cancellationToken)
            : new CiTicketHistory([], 0);

        var (lifecycle, lifecycleTotal) = kinds.Contains(CiTimelineEventKind.Lifecycle)
            ? await LoadLifecycleAsync(ciId, request, limit, cancellationToken)
            : ([], 0);

        var audit = kinds.Contains(CiTimelineEventKind.Config)
            ? await auditTrail.GetForEntityAsync(
                "Ci", ciId.ToString(), request.From, request.To, LifecycleActions, limit, cancellationToken)
            : new AuditTrail([], 0);

        return CiTimelineAssembler.Assemble(
            new CiTimelineSubject(
                ci.Id,
                ci.Name,
                kinds,
                request.From,
                request.To,
                alerts,
                tickets,
                lifecycle,
                lifecycleTotal,
                audit),
            limit);
    }

    /// <summary>
    /// The two lifecycle tables, merged into one source.
    /// <para>
    /// Each is capped at the limit and the merge is capped again, so a CI that changed hands two hundred
    /// times cannot crowd out its state transitions or vice versa. The total is the honest sum of both
    /// counts, so the row still states how much history is behind the cap.
    /// </para>
    /// </summary>
    private async Task<(IReadOnlyList<CiLifecycleEvent> Events, int Total)> LoadLifecycleAsync(
        Guid ciId,
        CiTimelineRequest request,
        int limit,
        CancellationToken cancellationToken)
    {
        var transitions = dbContext.CiLifecycleHistory.AsNoTracking().Where(entry => entry.CiId == ciId);
        var assignments = dbContext.CiAssignments.AsNoTracking().Where(entry => entry.CiId == ciId);

        if (request.From is { } from)
        {
            transitions = transitions.Where(entry => entry.OccurredAt >= from);
            assignments = assignments.Where(entry => entry.OccurredAt >= from);
        }

        if (request.To is { } to)
        {
            transitions = transitions.Where(entry => entry.OccurredAt <= to);
            assignments = assignments.Where(entry => entry.OccurredAt <= to);
        }

        var total = await transitions.CountAsync(cancellationToken)
            + await assignments.CountAsync(cancellationToken);

        var transitionRows = await transitions
            .OrderByDescending(entry => entry.OccurredAt)
            .ThenByDescending(entry => entry.Id)
            .Take(limit)
            .Select(entry => new
            {
                entry.Id,
                entry.OccurredAt,
                entry.ActorId,
                entry.FromState,
                entry.ToState,
                entry.Note,
            })
            .ToListAsync(cancellationToken);

        var assignmentRows = await assignments
            .OrderByDescending(entry => entry.OccurredAt)
            .ThenByDescending(entry => entry.Id)
            .Take(limit)
            .Select(entry => new
            {
                entry.Id,
                entry.OccurredAt,
                entry.ActorId,
                entry.Action,
                entry.FromOwnerName,
                entry.ToOwnerName,
                entry.DepartmentName,
                entry.SiteName,
                entry.Note,
            })
            .ToListAsync(cancellationToken);

        var events = transitionRows
            .Select(row => new CiLifecycleEvent(
                row.Id, row.OccurredAt, row.ActorId, row.FromState, row.ToState,
                Action: null, null, null, null, null, row.Note))
            .Concat(assignmentRows.Select(row => new CiLifecycleEvent(
                row.Id, row.OccurredAt, row.ActorId, null, null,
                row.Action, row.FromOwnerName, row.ToOwnerName, row.DepartmentName, row.SiteName, row.Note)))
            .OrderByDescending(entry => entry.OccurredAt)
            .ThenByDescending(entry => entry.Id)
            .Take(limit)
            .ToList();

        return (events, total);
    }
}
