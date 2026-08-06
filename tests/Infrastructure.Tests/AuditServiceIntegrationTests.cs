using System.Security.Claims;

using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

using Platform.Auditing;
using Platform.Data;

using Testcontainers.PostgreSql;

namespace Infrastructure.Tests;

public sealed class AuditServiceIntegrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("timescale/timescaledb-ha:pg17")
        .WithDatabase("it_platform")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();
    private PlatformDbContext? _dbContext;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseNpgsql(_postgres.GetConnectionString())
            .Options;
        _dbContext = new PlatformDbContext(options);
        await _dbContext.Database.MigrateAsync();
    }

    [Fact]
    public async Task WriteAsync_AuthenticatedActor_PersistsCompleteAuditEntry()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("sub", "admin-123")],
            "Test"));
        var httpContext = new DefaultHttpContext();
        httpContext.Items[CorrelationIdMiddleware.ItemKey] = "correlation-456";
        var service = new AuditService(_dbContext!, new HttpContextAccessor { HttpContext = httpContext });

        await service.WriteAsync(
            principal,
            "Updated",
            "TestEntity",
            "entity-789",
            new { value = "before" },
            new { value = "after" });

        var entry = await _dbContext!.AuditEntries.SingleAsync();
        Assert.Equal("admin-123", entry.ActorId);
        Assert.Equal("Updated", entry.Action);
        Assert.Equal("TestEntity", entry.EntityType);
        Assert.Equal("entity-789", entry.EntityId);
        Assert.Equal("{\"value\":\"before\"}", entry.BeforeJson);
        Assert.Equal("{\"value\":\"after\"}", entry.AfterJson);
        Assert.Equal("correlation-456", entry.CorrelationId);
        Assert.NotEqual(Guid.Empty, entry.Id);
    }

    [Fact]
    public void Migrations_CurrentModel_HasNoPendingChanges()
    {
        Assert.False(_dbContext!.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task SaveChanges_ModifiedAuditEntry_RejectsMutation()
    {
        var entry = new AuditEntry
        {
            Id = Guid.CreateVersion7(),
            ActorId = "admin-123",
            Action = "Created",
            EntityType = "TestEntity",
            EntityId = "entity-789",
            OccurredAt = DateTimeOffset.UtcNow,
            CorrelationId = "correlation-456",
        };
        _dbContext!.AuditEntries.Add(entry);
        await _dbContext.SaveChangesAsync();

        _dbContext.Entry(entry).Property(item => item.Action).CurrentValue = "Changed";

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _dbContext.SaveChangesAsync());
        Assert.Contains("append-only", exception.Message, StringComparison.Ordinal);
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
