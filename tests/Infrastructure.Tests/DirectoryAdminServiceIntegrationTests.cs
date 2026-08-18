using System.Security.Claims;

using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

using Platform.Auditing;
using Platform.Data;
using Platform.Directory;

using Testcontainers.PostgreSql;

namespace Infrastructure.Tests;

/// <summary>
/// Departments and locations as Settings edits them (Phase 5.5). The relationship is many-to-many
/// because the seeded estate already has Information Technology at three sites.
/// </summary>
public sealed class DirectoryAdminServiceIntegrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("timescale/timescaledb-ha:pg17")
        .WithDatabase("it_platform")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();
    private PlatformDbContext? _dbContext;

    private static readonly Guid HeadOffice = Guid.Parse("0198aaaa-0000-7000-8000-000000000001");
    private static readonly Guid DataCentre = Guid.Parse("0198aaaa-0000-7000-8000-000000000002");

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseNpgsql(_postgres.GetConnectionString())
            .Options;
        _dbContext = new PlatformDbContext(options);
        await _dbContext.Database.MigrateAsync();
        _dbContext.Sites.AddRange(
            new Site { Id = HeadOffice, Code = "HQ", Name = "Head Office" },
            new Site { Id = DataCentre, Code = "DC1", Name = "Primary Data Centre" });
        await _dbContext.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        if (_dbContext is not null) await _dbContext.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    private DirectoryAdminService Service(params IDirectoryUsageSource[] sources) =>
        new(_dbContext!, new AuditService(_dbContext!, new HttpContextAccessor { HttpContext = new DefaultHttpContext() }), sources);

    private static ClaimsPrincipal Admin() =>
        new(new ClaimsIdentity([new Claim("sub", "admin-1")], "Test"));

    [Fact]
    public void Migrations_CurrentModel_HasNoPendingChanges()
    {
        Assert.False(_dbContext!.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task CreateDepartment_WithTwoLocations_StoresBothAndAudits()
    {
        var result = await Service().CreateDepartmentAsync(
            new SaveDepartmentRequest("IT", "Information Technology", [HeadOffice, DataCentre]),
            Admin(),
            CancellationToken.None);

        Assert.Equal(DirectoryOutcome.Success, result.Outcome);
        Assert.Equal(["Head Office", "Primary Data Centre"], result.Department!.Sites.Select(site => site.Name));

        var audit = await _dbContext!.AuditEntries.SingleAsync(entry => entry.EntityType == "Department");
        Assert.Equal("Created", audit.Action);
    }

    /// <summary>The whole point of the many-to-many: one department present at several locations.</summary>
    [Fact]
    public async Task ListDepartments_DepartmentAtSeveralLocations_ReturnsEveryLocation()
    {
        var service = Service();
        await service.CreateDepartmentAsync(
            new SaveDepartmentRequest("IT", "Information Technology", [HeadOffice, DataCentre]),
            Admin(),
            CancellationToken.None);

        var departments = await service.ListDepartmentsAsync(CancellationToken.None);

        Assert.Equal(2, Assert.Single(departments).Sites.Count);
    }

    [Fact]
    public async Task UpdateDepartment_WithADifferentLocationSet_ReplacesItRatherThanAppending()
    {
        var service = Service();
        var created = await service.CreateDepartmentAsync(
            new SaveDepartmentRequest("IT", "Information Technology", [HeadOffice, DataCentre]),
            Admin(),
            CancellationToken.None);

        var updated = await service.UpdateDepartmentAsync(
            created.Department!.Id,
            new SaveDepartmentRequest("IT", "Information Technology", [DataCentre]),
            Admin(),
            CancellationToken.None);

        Assert.Equal(DirectoryOutcome.Success, updated.Outcome);
        Assert.Equal("Primary Data Centre", Assert.Single(updated.Department!.Sites).Name);
    }

    [Fact]
    public async Task UpdateSite_FromTheLocationSide_SetsTheSameLinks()
    {
        var service = Service();
        var department = await service.CreateDepartmentAsync(
            new SaveDepartmentRequest("FIN", "Finance", []),
            Admin(),
            CancellationToken.None);

        var updated = await service.UpdateSiteAsync(
            HeadOffice,
            new SaveSiteRequest("HQ", "Head Office", [department.Department!.Id]),
            Admin(),
            CancellationToken.None);

        Assert.Equal(DirectoryOutcome.Success, updated.Outcome);
        Assert.Equal("Finance", Assert.Single(updated.Site!.Departments).Name);

        var departments = await service.ListDepartmentsAsync(CancellationToken.None);
        Assert.Equal("Head Office", Assert.Single(Assert.Single(departments).Sites).Name);
    }

    /// <summary>FAILURE PATH: codes are the stable handle, so a duplicate has to be refused.</summary>
    [Fact]
    public async Task CreateDepartment_WithACodeThatExists_IsRefused()
    {
        var service = Service();
        await service.CreateDepartmentAsync(
            new SaveDepartmentRequest("IT", "Information Technology", []), Admin(), CancellationToken.None);

        var duplicate = await service.CreateDepartmentAsync(
            new SaveDepartmentRequest("it", "Something Else", []), Admin(), CancellationToken.None);

        Assert.Equal(DirectoryOutcome.DuplicateCode, duplicate.Outcome);
    }

    /// <summary>FAILURE PATH: a location id that does not exist must not create a dangling link.</summary>
    [Fact]
    public async Task CreateDepartment_WithAnUnknownLocation_IsRefusedAndStoresNothing()
    {
        var result = await Service().CreateDepartmentAsync(
            new SaveDepartmentRequest("IT", "Information Technology", [Guid.NewGuid()]),
            Admin(),
            CancellationToken.None);

        Assert.Equal(DirectoryOutcome.UnknownReference, result.Outcome);
        Assert.Empty(await _dbContext!.Departments.ToListAsync());
    }

    /// <summary>FAILURE PATH: people still in the department. Platform can see this one itself.</summary>
    [Fact]
    public async Task DeleteDepartment_WithPeopleInIt_IsRefused()
    {
        var service = Service();
        var department = await service.CreateDepartmentAsync(
            new SaveDepartmentRequest("IT", "Information Technology", []), Admin(), CancellationToken.None);
        _dbContext!.UserProfiles.Add(new UserProfile
        {
            Id = Guid.CreateVersion7(),
            Username = "someone",
            Email = "someone@example.test",
            DisplayName = "Some One",
            Role = "EndUser",
            SiteId = HeadOffice,
            DepartmentId = department.Department!.Id,
        });
        await _dbContext.SaveChangesAsync();

        var outcome = await service.DeleteDepartmentAsync(
            department.Department.Id, Admin(), CancellationToken.None);

        Assert.Equal(DirectoryOutcome.InUse, outcome);
        Assert.NotNull(await _dbContext.Departments.FindAsync(department.Department.Id));
    }

    /// <summary>
    /// FAILURE PATH: assets still pointing at it. Platform cannot see the assets schema, so this is
    /// the contribution interface earning its place — without it the delete would strand those CIs.
    /// </summary>
    [Fact]
    public async Task DeleteDepartment_WithConfigurationItemsOnIt_IsRefusedViaTheUsageSource()
    {
        var service = Service(new StubUsageSource(departmentCount: 3));
        var department = await service.CreateDepartmentAsync(
            new SaveDepartmentRequest("IT", "Information Technology", []), Admin(), CancellationToken.None);

        var outcome = await service.DeleteDepartmentAsync(
            department.Department!.Id, Admin(), CancellationToken.None);

        Assert.Equal(DirectoryOutcome.InUse, outcome);
    }

    [Fact]
    public async Task DeleteDepartment_ThatNothingUses_RemovesItAndItsLinks()
    {
        var service = Service(new StubUsageSource(departmentCount: 0));
        var department = await service.CreateDepartmentAsync(
            new SaveDepartmentRequest("IT", "Information Technology", [HeadOffice]),
            Admin(),
            CancellationToken.None);

        var outcome = await service.DeleteDepartmentAsync(
            department.Department!.Id, Admin(), CancellationToken.None);

        Assert.Equal(DirectoryOutcome.Success, outcome);
        Assert.Empty(await _dbContext!.DepartmentSites.ToListAsync());
        Assert.Equal(2, await _dbContext.Sites.CountAsync());
    }

    [Fact]
    public async Task DeleteSite_WithConfigurationItemsAtIt_IsRefusedViaTheUsageSource()
    {
        var outcome = await Service(new StubUsageSource(siteCount: 1))
            .DeleteSiteAsync(HeadOffice, Admin(), CancellationToken.None);

        Assert.Equal(DirectoryOutcome.InUse, outcome);
    }

    private sealed class StubUsageSource(int departmentCount = 0, int siteCount = 0) : IDirectoryUsageSource
    {
        public string ResourceName => "configuration items";

        public Task<int> CountByDepartmentAsync(Guid departmentId, CancellationToken cancellationToken) =>
            Task.FromResult(departmentCount);

        public Task<int> CountBySiteAsync(Guid siteId, CancellationToken cancellationToken) =>
            Task.FromResult(siteCount);
    }
}
