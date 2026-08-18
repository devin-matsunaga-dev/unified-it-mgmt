using Microsoft.EntityFrameworkCore;
using Modules.Helpdesk.Data;
using Modules.Helpdesk.Features.Tickets;
using Modules.Helpdesk.Features.Views;

namespace Modules.Helpdesk.Seeding;

public sealed record HelpdeskSeedResult(
    int TeamsAdded,
    int QueuesAdded,
    int MembersAdded,
    int CategoriesAdded,
    int CustomFieldsAdded,
    int CannedResponsesAdded,
    int ViewsAdded);

public sealed class HelpdeskDemoDataSeeder(HelpdeskDbContext dbContext)
{
    private static readonly Guid ServiceDeskTeamId = Guid.Parse("01980000-0000-7000-8000-000000000301");
    private static readonly Guid ServiceDeskQueueId = Guid.Parse("01980000-0000-7000-8000-000000000401");
    private static readonly string[] TechnicianIds = ["technician1", "technician2", "technician3", "technician4"];

    /// <summary>Top-level categories and their children, seeded so ticket forms open with a usable tree.</summary>
    private static readonly (Guid Id, string Name, Guid? ParentId, int SortOrder)[] Categories =
    [
        (Guid.Parse("01980000-0000-7000-8000-000000000501"), "Hardware", null, 1),
        (Guid.Parse("01980000-0000-7000-8000-000000000511"), "Laptop or desktop", Guid.Parse("01980000-0000-7000-8000-000000000501"), 1),
        (Guid.Parse("01980000-0000-7000-8000-000000000512"), "Printer", Guid.Parse("01980000-0000-7000-8000-000000000501"), 2),
        (Guid.Parse("01980000-0000-7000-8000-000000000502"), "Software", null, 2),
        (Guid.Parse("01980000-0000-7000-8000-000000000521"), "Email and calendar", Guid.Parse("01980000-0000-7000-8000-000000000502"), 1),
        (Guid.Parse("01980000-0000-7000-8000-000000000522"), "Business applications", Guid.Parse("01980000-0000-7000-8000-000000000502"), 2),
        (Guid.Parse("01980000-0000-7000-8000-000000000503"), "Access and accounts", null, 3),
        (Guid.Parse("01980000-0000-7000-8000-000000000531"), "Password reset", Guid.Parse("01980000-0000-7000-8000-000000000503"), 1),
        (Guid.Parse("01980000-0000-7000-8000-000000000532"), "New access request", Guid.Parse("01980000-0000-7000-8000-000000000503"), 2),
        (Guid.Parse("01980000-0000-7000-8000-000000000504"), "Network and connectivity", null, 4),
    ];

    /// <summary>
    /// Custom fields on seeded categories. The database is recreated on most AppHost restarts, so the
    /// fixture proving per-category fields work has to be seeded rather than created by hand.
    /// </summary>
    private static readonly (Guid Id, Guid CategoryId, string Key, string Label, CustomFieldType Type, bool IsRequired, string[] Options, int SortOrder)[] CustomFields =
    [
        (Guid.Parse("01980000-0000-7000-8000-000000000601"), Guid.Parse("01980000-0000-7000-8000-000000000511"),
            "asset_tag", "Asset tag", CustomFieldType.Text, true, [], 1),
        (Guid.Parse("01980000-0000-7000-8000-000000000602"), Guid.Parse("01980000-0000-7000-8000-000000000511"),
            "floor", "Floor", CustomFieldType.Select, false, ["Ground", "First", "Second"], 2),
    ];

    /// <summary>
    /// Starter macros. The dev database is recreated on most AppHost restarts, so the fixture proving
    /// placeholder substitution has to be seeded rather than created by hand.
    /// </summary>
    private static readonly (Guid Id, string Name, string Body)[] CannedResponses =
    [
        (Guid.Parse("01980000-0000-7000-8000-000000000701"), "Acknowledge receipt",
            "Hello {{requester.name}},\n\nThanks for contacting the service desk. Ticket {{ticket.number}} "
            + "(\"{{ticket.title}}\") is with me now and I will update you as soon as I have more.\n\n{{agent.name}}"),
        (Guid.Parse("01980000-0000-7000-8000-000000000702"), "Ask for more information",
            "Hello {{requester.name}},\n\nTo move {{ticket.number}} forward I need a little more detail: when the "
            + "problem started, and any error message you see on screen.\n\nThanks,\n{{agent.name}}"),
        (Guid.Parse("01980000-0000-7000-8000-000000000703"), "Resolution confirmation",
            "Hello {{requester.name}},\n\n{{ticket.number}} is resolved. Please reply within five working days if "
            + "the problem returns and this ticket will be reopened.\n\n{{agent.name}}"),
    ];

    /// <summary>
    /// One shared team view, so the picker demonstrates a view somebody else owns and shared.
    ///
    /// It is deliberately not one of the app's default views any more. A seeded view belongs to
    /// whoever seeded it, so the delete button — which only appears on your own — never showed for
    /// anybody else, and a default nobody can remove is not a default. Those live in the browser
    /// now; see the web app's ticketViews module.
    /// </summary>
    private static readonly (Guid Id, string Name, string OwnerId, string OwnerName, TicketListFilter Filter)[] Views =
    [
        (Guid.Parse("01980000-0000-7000-8000-000000000801"), "Escalations this week", "technician1", "Technician One",
            new TicketListFilter(
                Priorities: [TicketPriority.Critical], Statuses: ["New", "Triage", "InProgress"])),
    ];

    public async Task<HelpdeskSeedResult> SeedAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var teamsAdded = 0;
        var queuesAdded = 0;
        var membersAdded = 0;
        var categoriesAdded = 0;
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        if (!await dbContext.Teams.AnyAsync(team => team.Id == ServiceDeskTeamId, cancellationToken))
        {
            dbContext.Teams.Add(new Team { Id = ServiceDeskTeamId, Name = "Service Desk", CreatedAt = now });
            teamsAdded = 1;
        }

        foreach (var technicianId in TechnicianIds)
        {
            if (!await dbContext.TeamMembers.AnyAsync(
                    member => member.TeamId == ServiceDeskTeamId && member.TechnicianId == technicianId,
                    cancellationToken))
            {
                dbContext.TeamMembers.Add(new TeamMember
                {
                    TeamId = ServiceDeskTeamId,
                    TechnicianId = technicianId,
                    AddedAt = now,
                });
                membersAdded++;
            }
        }

        if (!await dbContext.TicketQueues.AnyAsync(queue => queue.Id == ServiceDeskQueueId, cancellationToken))
        {
            dbContext.TicketQueues.Add(new TicketQueue
            {
                Id = ServiceDeskQueueId,
                Name = "Service Desk",
                TeamId = ServiceDeskTeamId,
                CreatedAt = now,
            });
            queuesAdded = 1;
        }

        foreach (var (id, name, parentId, sortOrder) in Categories)
        {
            if (!await dbContext.TicketCategories.AnyAsync(category => category.Id == id, cancellationToken))
            {
                dbContext.TicketCategories.Add(new TicketCategory
                {
                    Id = id,
                    Name = name,
                    ParentId = parentId,
                    IsActive = true,
                    SortOrder = sortOrder,
                    CreatedAt = now,
                });
                categoriesAdded++;
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        var customFieldsAdded = 0;
        foreach (var (id, categoryId, key, label, type, isRequired, options, sortOrder) in CustomFields)
        {
            if (!await dbContext.TicketCustomFields.AnyAsync(field => field.Id == id, cancellationToken))
            {
                dbContext.TicketCustomFields.Add(new TicketCustomField
                {
                    Id = id,
                    CategoryId = categoryId,
                    Key = key,
                    Label = label,
                    Type = type,
                    IsRequired = isRequired,
                    Options = [.. options],
                    SortOrder = sortOrder,
                    CreatedAt = now,
                });
                customFieldsAdded++;
            }
        }

        var cannedResponsesAdded = 0;
        foreach (var (id, name, body) in CannedResponses)
        {
            if (!await dbContext.CannedResponses.AnyAsync(response => response.Id == id, cancellationToken))
            {
                dbContext.CannedResponses.Add(new CannedResponse
                {
                    Id = id,
                    Name = name,
                    Body = body,
                    CreatedById = "seeder",
                    CreatedAt = now,
                    UpdatedAt = now,
                });
                cannedResponsesAdded++;
            }
        }

        var viewsAdded = 0;
        foreach (var (id, name, ownerId, ownerName, filter) in Views)
        {
            if (!await dbContext.TicketViews.AnyAsync(view => view.Id == id, cancellationToken))
            {
                dbContext.TicketViews.Add(new TicketView
                {
                    Id = id,
                    Name = name,
                    OwnerId = ownerId,
                    OwnerDisplayName = ownerName,
                    IsShared = true,
                    FilterJson = TicketViewService.Serialize(filter),
                    CreatedAt = now,
                    UpdatedAt = now,
                });
                viewsAdded++;
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(
            teamsAdded, queuesAdded, membersAdded, categoriesAdded, customFieldsAdded,
            cannedResponsesAdded, viewsAdded);
    }
}
