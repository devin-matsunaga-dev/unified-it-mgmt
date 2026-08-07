using Microsoft.EntityFrameworkCore;

using Modules.Helpdesk.Data;
using Modules.Helpdesk.Seeding;

using Testcontainers.PostgreSql;

namespace Infrastructure.Tests;

public sealed class HelpdeskHistorySeederIntegrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("timescale/timescaledb-ha:pg17")
        .WithDatabase("it_platform")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();
    private HelpdeskDbContext? _dbContext;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        var options = new DbContextOptionsBuilder<HelpdeskDbContext>()
            .UseNpgsql(_postgres.GetConnectionString())
            .Options;
        _dbContext = new HelpdeskDbContext(options);
        await _dbContext.Database.MigrateAsync();
        await new HelpdeskDemoDataSeeder(_dbContext).SeedAsync();
    }

    [Fact]
    public async Task SeedAsync_RunTwice_CreatesOneTicketHistory()
    {
        var seeder = new HelpdeskHistorySeeder(_dbContext!);

        var first = await seeder.SeedAsync();
        var second = await seeder.SeedAsync();

        Assert.Equal(1, first.CalendarsAdded);
        Assert.Equal(4, first.PoliciesAdded);
        Assert.Equal(HelpdeskHistorySeeder.TicketCount, first.TicketsAdded);
        Assert.Equal(HelpdeskHistorySeeder.TicketCount, first.SlasAdded);
        Assert.True(first.CommentsAdded > 0);
        Assert.True(first.WorklogsAdded > 0);
        Assert.True(first.TransitionsAdded > 0);
        Assert.Equal(new HelpdeskHistorySeedResult(0, 0, 0, 0, 0, 0, 0), second);
        Assert.Equal(HelpdeskHistorySeeder.TicketCount, await _dbContext!.Tickets.CountAsync());
        Assert.Equal(HelpdeskHistorySeeder.TicketCount, await _dbContext.TicketSlas.CountAsync());
        Assert.Equal(first.CommentsAdded, await _dbContext.TicketComments.CountAsync());
        Assert.Equal(first.WorklogsAdded, await _dbContext.TicketWorklogs.CountAsync());
        Assert.Equal(first.TransitionsAdded, await _dbContext.TicketTransitionHistory.CountAsync());
    }

    [Fact]
    public async Task SeedAsync_AfterSeeding_CoversEveryStatusAgeAndSlaState()
    {
        await new HelpdeskHistorySeeder(_dbContext!).SeedAsync();

        var statusIds = await _dbContext!.Tickets.Select(ticket => ticket.StatusId).Distinct().ToListAsync();
        Assert.Equal(6, statusIds.Count);
        Assert.True(await _dbContext.Tickets.AnyAsync(ticket => ticket.AssignedTechnicianId == null));
        Assert.True(await _dbContext.Tickets.AnyAsync(ticket => ticket.AssignedTechnicianId != null));
        Assert.True(await _dbContext.TicketComments.AnyAsync(comment => comment.IsInternal));
        Assert.True(await _dbContext.TicketComments.AnyAsync(comment => !comment.IsInternal));
        Assert.Equal(4, await _dbContext.Tickets.Select(ticket => ticket.Priority).Distinct().CountAsync());

        var oldest = await _dbContext.Tickets.MinAsync(ticket => ticket.CreatedAt);
        var newest = await _dbContext.Tickets.MaxAsync(ticket => ticket.CreatedAt);
        Assert.True((newest - oldest).TotalDays > 60, "seeded tickets should span more than two months");

        Assert.True(await _dbContext.TicketSlas.AnyAsync(sla => sla.ResponseBreached));
        Assert.True(await _dbContext.TicketSlas.AnyAsync(sla => sla.ResolutionBreached));
        Assert.True(await _dbContext.TicketSlas.AnyAsync(
            sla => !sla.ResponseBreached && !sla.ResolutionBreached && sla.ResolutionCompletedAt == null));
    }

    [Fact]
    public async Task SeedAsync_ClosedTickets_StopTheSlaClock()
    {
        await new HelpdeskHistorySeeder(_dbContext!).SeedAsync();

        var running = await _dbContext!.TicketSlas.Include(sla => sla.Ticket)
            .Where(sla => sla.ResolutionCompletedAt == null)
            .Select(sla => sla.Ticket.StatusId).Distinct().ToListAsync();

        Assert.DoesNotContain(DefaultTicketStatuses.ResolvedId, running);
        Assert.DoesNotContain(DefaultTicketStatuses.ClosedId, running);
        // A breached SLA must already carry its flag, or the evaluation job would re-escalate seeded history.
        Assert.Empty(await _dbContext.TicketSlas.Include(sla => sla.Policy)
            .Where(sla => sla.ResolutionCompletedAt == null && !sla.ResolutionBreached
                && sla.AccumulatedBusinessSeconds >= sla.Policy.ResolutionTargetMinutes * 60d)
            .ToListAsync());
    }

    [Fact]
    public async Task SaveChanges_SeededTicketWithUnknownCategory_RejectsInvalidData()
    {
        await new HelpdeskHistorySeeder(_dbContext!).SeedAsync();
        _dbContext!.Tickets.Add(new Ticket
        {
            Id = Guid.CreateVersion7(),
            Title = "Ticket in a category that was never seeded",
            Description = "The category link must be enforced by the database, not only by the seeder.",
            Type = TicketType.Incident,
            Urgency = TicketLevel.Low,
            Impact = TicketLevel.Low,
            Priority = TicketPriority.Low,
            StatusId = DefaultTicketStatuses.NewId,
            RequesterId = "enduser1",
            CategoryId = Guid.Parse("01980099-0000-7000-8000-000000000999"),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => _dbContext.SaveChangesAsync());

        Assert.Contains("fk_tickets_ticket_categories_category_id", exception.InnerException?.Message, StringComparison.Ordinal);
    }

    public async Task DisposeAsync()
    {
        if (_dbContext is not null)
        {
            await _dbContext.DisposeAsync();
        }

        await _postgres.DisposeAsync();
    }
}
