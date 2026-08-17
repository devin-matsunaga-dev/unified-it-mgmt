namespace Contracts.Events;

/// <summary>
/// Somebody with authority agreed that a set of CIs may be disturbed between two instants.
/// <para>
/// Published by Assets when a change request is approved (WP-5.8). Monitoring consumes it and opens a
/// maintenance window over whichever of those CIs it polls, so the alerts the work itself causes are
/// muted for exactly as long as the work was agreed to take.
/// </para>
/// <para>
/// An event and not a port, and the reason is the same one that shaped
/// <see cref="DiscoveredDeviceApproved"/>: Assets owns CIs, Monitoring owns maintenance windows, neither
/// module may reference the other, and ARCHITECTURE §3 is explicit that a port is a read surface and
/// never a write path. Opening a window is a write in <c>monitoring</c>'s schema, so the approval crosses
/// the boundary as a fact on the bus.
/// </para>
/// </summary>
/// <param name="ChangeRequestId">
/// The request that was approved. Carried onto the window Monitoring creates, which is what makes the
/// sync idempotent — one window per change, enforced by a filtered unique index rather than by hope.
/// </param>
/// <param name="Number">
/// The human reference (<c>CHG-000007</c>). It names the window, because an operator looking at a muted
/// device needs to be able to find the change that muted it without a lookup.
/// </param>
/// <param name="StartsAt">
/// The agreed start. May already be in the past when this is consumed — a change approved as the work
/// begins is the normal case, not an error — so the window opens immediately rather than being refused.
/// </param>
/// <param name="CiIds">
/// Every CI the change covers: the ones named on the request, plus their dependents where the requester
/// asked for them. Resolved once, at approval, and carried here rather than re-walked by the consumer —
/// Monitoring cannot read <c>assets.ci_relationships</c>, and a graph that gained an edge overnight must
/// not silently widen a window somebody already agreed to.
/// </param>
public sealed record ChangeRequestApproved(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid ChangeRequestId,
    string Number,
    string Title,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    IReadOnlyList<Guid> CiIds);
