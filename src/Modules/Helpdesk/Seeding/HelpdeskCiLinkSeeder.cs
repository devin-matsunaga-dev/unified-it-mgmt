using Microsoft.EntityFrameworkCore;

using Modules.Helpdesk.Data;

namespace Modules.Helpdesk.Seeding;

/// <summary>
/// The configuration items worth attaching to seeded tickets, grouped by the kind of ticket that would
/// name them. Helpdesk owns ticket↔CI links but may not reference the Assets module, so the ids arrive
/// from the caller — the same rule that put <c>ICiDirectory</c> in <c>Platform/Integration</c>.
/// </summary>
public sealed record CiLinkPlan(
    IReadOnlyList<Guid> HardwareCiIds,
    IReadOnlyList<Guid> NetworkCiIds,
    IReadOnlyList<Guid> ServiceCiIds);

public sealed record HelpdeskCiLinkSeedResult(int LinksAdded);

/// <summary>
/// Links a slice of the seeded backlog to the seeded estate, so the ticket, asset and 360° pages all
/// have the cross-module context they exist to show. Tickets are picked by category — a laptop ticket
/// gets a laptop, a connectivity ticket gets a switch, an application ticket gets a business service —
/// and the newest ones are chosen so the links sit on tickets an agent is likely to open.
/// </summary>
public sealed class HelpdeskCiLinkSeeder(HelpdeskDbContext dbContext)
{
    /// <summary>Tickets linked per category. Enough to be visible on the lists without linking everything.</summary>
    public const int TicketsPerCategory = 6;

    private const int LinkKind = 9;
    private const string ActorId = "seeder";
    private const string ActorName = "Demo seeder";

    private static readonly Guid LaptopCategoryId = Guid.Parse("01980000-0000-7000-8000-000000000511");
    private static readonly Guid NetworkCategoryId = Guid.Parse("01980000-0000-7000-8000-000000000504");
    private static readonly Guid ApplicationCategoryId = Guid.Parse("01980000-0000-7000-8000-000000000522");

    public async Task<HelpdeskCiLinkSeedResult> SeedAsync(CiLinkPlan plan, CancellationToken cancellationToken = default)
    {
        var groups = new[]
        {
            (CategoryId: LaptopCategoryId, CiIds: plan.HardwareCiIds),
            (CategoryId: NetworkCategoryId, CiIds: plan.NetworkCiIds),
            (CategoryId: ApplicationCategoryId, CiIds: plan.ServiceCiIds),
        };

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var added = 0;
        var index = 0;
        var now = DateTimeOffset.UtcNow;

        foreach (var (categoryId, ciIds) in groups)
        {
            if (ciIds.Count == 0)
            {
                index += TicketsPerCategory;
                continue;
            }

            var tickets = await dbContext.Tickets
                .Where(ticket => ticket.CategoryId == categoryId)
                .OrderByDescending(ticket => ticket.CreatedAt)
                .ThenBy(ticket => ticket.Id)
                .Take(TicketsPerCategory)
                .Select(ticket => new { ticket.Id, ticket.CreatedAt })
                .ToListAsync(cancellationToken);

            for (var position = 0; position < TicketsPerCategory; position++)
            {
                // The id advances whether or not a ticket was found, so a category that gains tickets
                // later does not renumber the links already written for the categories after it.
                var linkId = DeterministicId(LinkKind, index++);
                if (position >= tickets.Count
                    || await dbContext.TicketCiLinks.AnyAsync(link => link.Id == linkId, cancellationToken))
                {
                    continue;
                }

                var ticket = tickets[position];
                dbContext.TicketCiLinks.Add(new TicketCiLink
                {
                    Id = linkId,
                    TicketId = ticket.Id,
                    CiId = ciIds[position % ciIds.Count],
                    LinkedById = ActorId,
                    LinkedByName = ActorName,
                    // Linked shortly after the ticket arrived, never in the future.
                    LinkedAt = ticket.CreatedAt + TimeSpan.FromMinutes(20) > now
                        ? now
                        : ticket.CreatedAt + TimeSpan.FromMinutes(20),
                });
                added++;
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new HelpdeskCiLinkSeedResult(added);
    }

    private static Guid DeterministicId(int kind, int index) =>
        Guid.Parse($"01980001-{kind:0000}-7000-8000-{index:0000}00000000");
}
