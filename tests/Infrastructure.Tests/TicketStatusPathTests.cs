using Modules.Helpdesk.Data;
using Modules.Helpdesk.Features.AlertTickets;

namespace Infrastructure.Tests;

/// <summary>
/// The route an auto-resolve takes, without a database. This is where "auto-resolve" stops being one
/// word: the WP-1.2 graph is a linear chain, so a ticket at New is four guarded transitions away from
/// Resolved and every one of them has to happen.
/// </summary>
public sealed class TicketStatusPathTests
{
    /// <summary>The graph WP-1.2's migration seeds, which is the one the automation actually walks.</summary>
    private static readonly (Guid From, Guid To)[] SeededGraph =
    [
        (DefaultTicketStatuses.NewId, DefaultTicketStatuses.TriageId),
        (DefaultTicketStatuses.TriageId, DefaultTicketStatuses.InProgressId),
        (DefaultTicketStatuses.InProgressId, DefaultTicketStatuses.PendingId),
        (DefaultTicketStatuses.PendingId, DefaultTicketStatuses.ResolvedId),
        (DefaultTicketStatuses.ResolvedId, DefaultTicketStatuses.ClosedId),
    ];

    [Fact]
    public void Find_FromNewToResolved_WalksEveryHopOfTheSeededChain()
    {
        var path = TicketStatusPath.Find(
            SeededGraph, DefaultTicketStatuses.NewId, DefaultTicketStatuses.ResolvedId);

        Assert.Equal(
            [
                DefaultTicketStatuses.TriageId,
                DefaultTicketStatuses.InProgressId,
                DefaultTicketStatuses.PendingId,
                DefaultTicketStatuses.ResolvedId,
            ],
            path);
    }

    [Fact]
    public void Find_FromInProgressToResolved_StartsWhereTheTicketAlreadyIs()
    {
        var path = TicketStatusPath.Find(
            SeededGraph, DefaultTicketStatuses.InProgressId, DefaultTicketStatuses.ResolvedId);

        Assert.Equal([DefaultTicketStatuses.PendingId, DefaultTicketStatuses.ResolvedId], path);
    }

    /// <summary>A ticket already there needs no transition, which is not the same as no route.</summary>
    [Fact]
    public void Find_FromAStatusToItself_IsAnEmptyPathRatherThanNull()
    {
        var path = TicketStatusPath.Find(
            SeededGraph, DefaultTicketStatuses.ResolvedId, DefaultTicketStatuses.ResolvedId);

        Assert.NotNull(path);
        Assert.Empty(path);
    }

    /// <summary>
    /// The failure path that matters: nothing leaves Closed, so an automation asked to resolve a
    /// closed ticket must find no route and leave it alone rather than walk into a 409 per hop.
    /// </summary>
    [Fact]
    public void Find_FromClosed_FindsNoRouteBackToResolved()
    {
        var path = TicketStatusPath.Find(
            SeededGraph, DefaultTicketStatuses.ClosedId, DefaultTicketStatuses.ResolvedId);

        Assert.Null(path);
    }

    /// <summary>
    /// Read from the graph rather than hardcoded, so a shortcut somebody adds later is used. If this
    /// ever fails, the automation has stopped following the workflow the database describes.
    /// </summary>
    [Fact]
    public void Find_WithAShortcutEdge_PrefersTheShorterRoute()
    {
        (Guid, Guid)[] withShortcut =
        [
            .. SeededGraph,
            (DefaultTicketStatuses.NewId, DefaultTicketStatuses.ResolvedId),
        ];

        var path = TicketStatusPath.Find(
            withShortcut, DefaultTicketStatuses.NewId, DefaultTicketStatuses.ResolvedId);

        Assert.Equal([DefaultTicketStatuses.ResolvedId], path);
    }

    [Fact]
    public void Find_WithNoEdges_Throws() =>
        Assert.Throws<ArgumentNullException>(() =>
            TicketStatusPath.Find(null!, DefaultTicketStatuses.NewId, DefaultTicketStatuses.ResolvedId));
}
