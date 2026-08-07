using Microsoft.EntityFrameworkCore;

using Platform.Data;
using Platform.Seeding;
using Modules.Helpdesk.Data;
using Modules.Helpdesk.Seeding;

using Testcontainers.PostgreSql;

namespace Infrastructure.Tests;

public sealed class DemoDataSeederIntegrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("timescale/timescaledb-ha:pg17")
        .WithDatabase("it_platform")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();
    private PlatformDbContext? _dbContext;
    private HelpdeskDbContext? _helpdeskDbContext;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseNpgsql(_postgres.GetConnectionString())
            .Options;
        _dbContext = new PlatformDbContext(options);
        await _dbContext.Database.MigrateAsync();
        var helpdeskOptions = new DbContextOptionsBuilder<HelpdeskDbContext>()
            .UseNpgsql(_postgres.GetConnectionString())
            .Options;
        _helpdeskDbContext = new HelpdeskDbContext(helpdeskOptions);
        await _helpdeskDbContext.Database.MigrateAsync();
    }

    [Fact]
    public async Task SeedAsync_RunTwice_CreatesOneStableDataset()
    {
        var seeder = new DemoDataSeeder(_dbContext!);

        var first = await seeder.SeedAsync();
        var second = await seeder.SeedAsync();

        Assert.Equal(new SeedResult(3, 4, 20), first);
        Assert.Equal(new SeedResult(0, 0, 0), second);
        Assert.Equal(3, await _dbContext!.Sites.CountAsync());
        Assert.Equal(4, await _dbContext.Departments.CountAsync());
        Assert.Equal(20, await _dbContext.UserProfiles.CountAsync());
        Assert.Equal(4, await _dbContext.UserProfiles.Select(user => user.Role).Distinct().CountAsync());
    }

    [Fact]
    public async Task SaveChanges_UserWithUnsupportedRole_RejectsInvalidData()
    {
        await new DemoDataSeeder(_dbContext!).SeedAsync();
        var siteId = await _dbContext!.Sites.Select(site => site.Id).FirstAsync();
        var departmentId = await _dbContext.Departments.Select(department => department.Id).FirstAsync();
        _dbContext.UserProfiles.Add(new UserProfile
        {
            Id = Guid.CreateVersion7(),
            Username = "invalid-role-user",
            Email = "invalid-role-user@example.test",
            DisplayName = "Invalid Role User",
            Role = "Superuser",
            SiteId = siteId,
            DepartmentId = departmentId,
        });

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => _dbContext.SaveChangesAsync());

        Assert.Contains("ck_user_profiles_role", exception.InnerException?.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HelpdeskSeedAsync_RunTwice_CreatesServiceDeskQueueAndMembersOnce()
    {
        var seeder = new HelpdeskDemoDataSeeder(_helpdeskDbContext!);

        var first = await seeder.SeedAsync();
        var second = await seeder.SeedAsync();

        Assert.Equal(new HelpdeskSeedResult(1, 1, 4, 10, 2), first);
        Assert.Equal(new HelpdeskSeedResult(0, 0, 0, 0, 0), second);
        Assert.Equal("Service Desk", await _helpdeskDbContext!.TicketQueues.Select(queue => queue.Name).SingleAsync());
        var seededField = await _helpdeskDbContext.TicketCustomFields
            .Include(field => field.Category)
            .SingleAsync(field => field.Key == "asset_tag");
        Assert.Equal("Laptop or desktop", seededField.Category.Name);
        Assert.True(seededField.IsRequired);
        Assert.Equal(
            ["technician1", "technician2", "technician3", "technician4"],
            await _helpdeskDbContext.TeamMembers.OrderBy(member => member.TechnicianId)
                .Select(member => member.TechnicianId).ToListAsync());
    }

    public async Task DisposeAsync()
    {
        if (_dbContext is not null)
        {
            await _dbContext.DisposeAsync();
        }

        if (_helpdeskDbContext is not null)
        {
            await _helpdeskDbContext.DisposeAsync();
        }

        await _postgres.DisposeAsync();
    }
}
