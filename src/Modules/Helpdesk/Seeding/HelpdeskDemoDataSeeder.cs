using Microsoft.EntityFrameworkCore;
using Modules.Helpdesk.Data;

namespace Modules.Helpdesk.Seeding;

public sealed record HelpdeskSeedResult(int TeamsAdded, int QueuesAdded, int MembersAdded);

public sealed class HelpdeskDemoDataSeeder(HelpdeskDbContext dbContext)
{
    private static readonly Guid ServiceDeskTeamId = Guid.Parse("01980000-0000-7000-8000-000000000301");
    private static readonly Guid ServiceDeskQueueId = Guid.Parse("01980000-0000-7000-8000-000000000401");
    private static readonly string[] TechnicianIds = ["technician1", "technician2", "technician3", "technician4"];

    public async Task<HelpdeskSeedResult> SeedAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var teamsAdded = 0;
        var queuesAdded = 0;
        var membersAdded = 0;
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

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(teamsAdded, queuesAdded, membersAdded);
    }
}
