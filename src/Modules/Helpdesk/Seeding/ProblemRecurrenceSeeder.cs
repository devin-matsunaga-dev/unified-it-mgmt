using Microsoft.EntityFrameworkCore;

using Modules.Helpdesk.Data;

namespace Modules.Helpdesk.Seeding;

public sealed record ProblemRecurrenceSeedResult(int TicketsAdded, int LinksAdded, Guid? CiId);

/// <summary>
/// A deliberate recurrence: five recent incidents about one switch, so the WP's own verification step
/// — "seed 5 similar incidents on one switch → suggestion appears" — is walkable a minute after
/// <c>aspire run</c> with nothing to type.
/// <para>
/// It is a seeder of its own rather than a change to <see cref="HelpdeskHistorySeeder"/>, whose 200
/// tickets are spread deliberately across categories, ages and statuses. Concentrating five of them on
/// one CI would have made the recurrence a side effect of a distribution nobody would think to protect,
/// and any later change to those bands would silently un-seed this package's demo. Five extra tickets on
/// a fixed CI, with fixed ids and fixed ages, is a fact somebody can read.
/// </para>
/// <para>
/// Helpdesk owns ticket↔CI links but may not reference the Assets module, so the CI ids arrive from the
/// caller — the same rule that shaped <see cref="HelpdeskCiLinkSeeder"/>.
/// </para>
/// </summary>
public sealed class ProblemRecurrenceSeeder(HelpdeskDbContext dbContext)
{
    /// <summary>
    /// Five, because that is the detector's default threshold and the number the WP names. Seeding
    /// exactly the threshold rather than comfortably above it means a change to either is caught by the
    /// demo going quiet rather than by nobody noticing.
    /// </summary>
    public const int IncidentCount = 5;

    private const int TicketKind = 57;
    private const int LinkKind = 58;
    private const string ActorId = "seeder";
    private const string ActorName = "Demo seeder";

    /// <summary>The seeded "Network / connectivity" category, from <see cref="HelpdeskDemoDataSeeder"/>.</summary>
    private static readonly Guid NetworkCategoryId = Guid.Parse("01980000-0000-7000-8000-000000000504");

    /// <summary>
    /// Five reports of the same fault in the words five different people would use. Deliberately not five
    /// copies of one sentence: the knowledge draft groups identical titles, and a demo in which every
    /// symptom collapses into one line does not show what that grouping is for.
    /// </summary>
    private static readonly (string Title, string Description, TicketLevel Urgency, string RequesterId, string RequesterName)[] Incidents =
    [
        ("Wi-Fi keeps dropping on the second floor",
            "Everyone on this floor loses the network for about a minute, several times an hour. Reconnecting works but it happens again.",
            TicketLevel.High, "enduser2", "End User Two"),
        ("Network drops for a minute at a time",
            "My machine loses the network briefly and then comes back. It has happened four times since this morning.",
            TicketLevel.Medium, "enduser4", "End User Four"),
        ("Wi-Fi keeps dropping on the second floor",
            "Same as the tickets my colleagues have raised — the connection goes for around a minute and then returns on its own.",
            TicketLevel.Medium, "enduser7", "End User Seven"),
        ("Cannot reach the file share intermittently",
            "The file share disappears from the network for short periods. It is fine again by the time anybody looks.",
            TicketLevel.High, "enduser9", "End User Nine"),
        ("Video calls cut out several times a day",
            "Calls freeze and then reconnect. My phone on mobile data in the same room is unaffected, so it looks like the network here.",
            TicketLevel.High, "enduser1", "End User One"),
    ];

    /// <param name="networkCiIds">
    /// The estate's network devices. The first is used, and only the first — the whole point is that the
    /// incidents land on one thing.
    /// </param>
    public async Task<ProblemRecurrenceSeedResult> SeedAsync(
        IReadOnlyList<Guid> networkCiIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(networkCiIds);
        if (networkCiIds.Count == 0)
        {
            return new ProblemRecurrenceSeedResult(0, 0, null);
        }

        var ciId = networkCiIds[0];
        var categoryExists = await dbContext.TicketCategories
            .AnyAsync(category => category.Id == NetworkCategoryId, cancellationToken);

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var ticketsAdded = 0;
        var linksAdded = 0;

        for (var index = 0; index < Incidents.Length; index++)
        {
            var ticketId = DeterministicId(TicketKind, index);
            if (await dbContext.Tickets.AnyAsync(ticket => ticket.Id == ticketId, cancellationToken))
            {
                continue;
            }

            var (title, description, urgency, requesterId, requesterName) = Incidents[index];
            // Spread across the last five days, newest first, so every one of them falls inside the
            // detector's default seven-day window with room to spare.
            var createdAt = now - TimeSpan.FromDays(index + 0.5);
            dbContext.Tickets.Add(new Ticket
            {
                Id = ticketId,
                Title = title,
                Description = description,
                Type = TicketType.Incident,
                Urgency = urgency,
                Impact = TicketLevel.Medium,
                Priority = urgency == TicketLevel.High ? TicketPriority.High : TicketPriority.Medium,
                StatusId = index == 0 ? DefaultTicketStatuses.NewId : DefaultTicketStatuses.TriageId,
                RequesterId = requesterId,
                RequesterDisplayName = requesterName,
                CategoryId = categoryExists ? NetworkCategoryId : null,
                CreatedAt = createdAt,
                UpdatedAt = createdAt,
            });
            ticketsAdded++;

            dbContext.TicketCiLinks.Add(new TicketCiLink
            {
                Id = DeterministicId(LinkKind, index),
                TicketId = ticketId,
                CiId = ciId,
                LinkedById = ActorId,
                LinkedByName = ActorName,
                LinkedAt = createdAt + TimeSpan.FromMinutes(15),
            });
            linksAdded++;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new ProblemRecurrenceSeedResult(ticketsAdded, linksAdded, ciId);
    }

    private static Guid DeterministicId(int kind, int index) =>
        Guid.Parse($"01980001-{kind:0000}-7000-8000-{index:0000}00000000");
}
