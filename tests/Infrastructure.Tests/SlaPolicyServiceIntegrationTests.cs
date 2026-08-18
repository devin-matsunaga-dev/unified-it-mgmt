using System.Security.Claims;

using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

using Modules.Helpdesk.Data;
using Modules.Helpdesk.Features.Sla;
using MassTransit;
using Microsoft.Extensions.Options;
using Platform.Auditing;
using Platform.Data;
using Platform.Notifications;

using Testcontainers.PostgreSql;

namespace Infrastructure.Tests;

/// <summary>
/// SLA policies as Settings edits them.
///
/// <para>
/// Its own database rather than the shared ticket host, deliberately: policy matching is global, so
/// a test that adds a policy with unusual targets changes which policy every other test's ticket
/// gets. That contamination is invisible until an unrelated assertion fails.
/// </para>
/// </summary>
public sealed class SlaPolicyServiceIntegrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("timescale/timescaledb-ha:pg17")
        .WithDatabase("it_platform")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();
    private HelpdeskDbContext? _helpdesk;
    private PlatformDbContext? _platform;
    private Guid _calendarId;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        _platform = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>()
            .UseNpgsql(_postgres.GetConnectionString()).Options);
        await _platform.Database.MigrateAsync();
        _helpdesk = new HelpdeskDbContext(new DbContextOptionsBuilder<HelpdeskDbContext>()
            .UseNpgsql(_postgres.GetConnectionString()).Options);
        await _helpdesk.Database.MigrateAsync();

        _calendarId = Guid.CreateVersion7();
        _helpdesk.BusinessHoursCalendars.Add(new BusinessHoursCalendar
        {
            Id = _calendarId,
            Name = "24x7",
            TimeZoneId = "UTC",
            WorkingDays = BusinessDays.Weekdays | BusinessDays.Saturday | BusinessDays.Sunday,
            StartTime = new TimeOnly(0, 0),
            EndTime = new TimeOnly(23, 59),
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await _helpdesk.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        if (_helpdesk is not null) await _helpdesk.DisposeAsync();
        if (_platform is not null) await _platform.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    /**
     * Nothing here exercises publishing or notifying — these tests are about which policy applies and
     * what it leaves on the ticket — so both are satisfied with doubles that record and do nothing.
     */
    private SlaService Service() => new(
        _helpdesk!,
        new NoOpPublishEndpoint(),
        new NoOpRouter(),
        Options.Create(new NotificationOptions()),
        new AuditService(_platform!, new HttpContextAccessor { HttpContext = new DefaultHttpContext() }));

    private static ClaimsPrincipal Admin() =>
        new(new ClaimsIdentity([new Claim("sub", "admin-1")], "Test"));

    private SavePolicyRequest Request(
        string name,
        int resolutionMinutes,
        TicketPriority? priority = null,
        TicketType? type = null,
        Guid? categoryId = null,
        int sortOrder = 0) =>
        new(name, 5, resolutionMinutes, 80, _calendarId, priority, type, categoryId, sortOrder);

    private async Task<Ticket> AddTicketAsync(
        TicketPriority priority = TicketPriority.Critical,
        TicketType type = TicketType.Incident,
        Guid? categoryId = null)
    {
        var ticket = new Ticket
        {
            Id = Guid.CreateVersion7(),
            Title = "A ticket",
            Description = "Something happened.",
            Type = type,
            Priority = priority,
            CategoryId = categoryId,
            RequesterId = "enduser1",
            StatusId = DefaultTicketStatuses.NewId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        _helpdesk!.Tickets.Add(ticket);
        await _helpdesk.SaveChangesAsync();
        return ticket;
    }

    [Fact]
    public void Migrations_CurrentModel_HasNoPendingChanges()
    {
        Assert.False(_helpdesk!.Database.HasPendingModelChanges());
    }

    /// <summary>A narrow rule above the catch-all wins; the same rule below it never runs.</summary>
    [Fact]
    public async Task StartAsync_AttachesTheFirstMatchingPolicyInOrder()
    {
        var service = Service();
        await service.CreatePolicyAsync(Request("Catch-all", 999, sortOrder: 10), Admin(), default);
        await service.CreatePolicyAsync(
            Request("Critical incidents", 7, TicketPriority.Critical, sortOrder: 1), Admin(), default);

        var ticket = await AddTicketAsync();
        await service.StartAsync(ticket, DateTimeOffset.UtcNow, default);
        await _helpdesk!.SaveChangesAsync();

        var sla = await _helpdesk.TicketSlas.SingleAsync(item => item.TicketId == ticket.Id);
        Assert.Equal(7, sla.ResolutionTargetMinutes);
    }

    /// <summary>Order beats creation time, which is the reason it is a column an administrator sets.</summary>
    [Fact]
    public async Task StartAsync_OrderBeatsCreationTime()
    {
        var service = Service();
        // Created first, ordered last.
        await service.CreatePolicyAsync(
            Request("Created first", 111, TicketPriority.Critical, sortOrder: 90), Admin(), default);
        await service.CreatePolicyAsync(
            Request("Created second", 22, TicketPriority.Critical, sortOrder: 1), Admin(), default);

        var ticket = await AddTicketAsync();
        await service.StartAsync(ticket, DateTimeOffset.UtcNow, default);
        await _helpdesk!.SaveChangesAsync();

        Assert.Equal(22, (await _helpdesk.TicketSlas.SingleAsync(item => item.TicketId == ticket.Id))
            .ResolutionTargetMinutes);
    }

    [Fact]
    public async Task StartAsync_MatchesOnTicketTypeAndCategory()
    {
        var category = new TicketCategory
        {
            Id = Guid.CreateVersion7(), Name = "Network", IsActive = true, CreatedAt = DateTimeOffset.UtcNow,
        };
        _helpdesk!.TicketCategories.Add(category);
        await _helpdesk.SaveChangesAsync();

        var service = Service();
        await service.CreatePolicyAsync(Request("Catch-all", 999, sortOrder: 50), Admin(), default);
        await service.CreatePolicyAsync(
            Request("Network requests", 33, type: TicketType.ServiceRequest, categoryId: category.Id, sortOrder: 1),
            Admin(), default);

        var matching = await AddTicketAsync(type: TicketType.ServiceRequest, categoryId: category.Id);
        var wrongType = await AddTicketAsync(type: TicketType.Incident, categoryId: category.Id);
        foreach (var ticket in new[] { matching, wrongType })
        {
            await service.StartAsync(ticket, DateTimeOffset.UtcNow, default);
        }

        await _helpdesk.SaveChangesAsync();

        Assert.Equal(33, (await _helpdesk.TicketSlas.SingleAsync(item => item.TicketId == matching.Id))
            .ResolutionTargetMinutes);
        Assert.Equal(999, (await _helpdesk.TicketSlas.SingleAsync(item => item.TicketId == wrongType.Id))
            .ResolutionTargetMinutes);
    }

    /// <summary>A ticket nothing matches simply has no clock, rather than an invented one.</summary>
    [Fact]
    public async Task StartAsync_WithNoMatchingPolicy_AttachesNothing()
    {
        var service = Service();
        await service.CreatePolicyAsync(Request("Low only", 60, TicketPriority.Low), Admin(), default);

        var ticket = await AddTicketAsync(TicketPriority.Critical);
        await service.StartAsync(ticket, DateTimeOffset.UtcNow, default);
        await _helpdesk!.SaveChangesAsync();

        Assert.False(await _helpdesk.TicketSlas.AnyAsync(item => item.TicketId == ticket.Id));
    }

    /// <summary>
    /// The reason targets are copied onto the ticket. Editing a policy used to re-target everything
    /// already running against it — tighten a target at lunchtime and work that was on track is
    /// retrospectively breached, with no record that anything moved.
    /// </summary>
    [Fact]
    public async Task UpdatePolicy_LeavesTicketsAlreadyRunningOnTheirOriginalTargets()
    {
        var service = Service();
        var created = await service.CreatePolicyAsync(
            Request("Original", 600, TicketPriority.Critical), Admin(), default);
        var ticket = await AddTicketAsync();
        await service.StartAsync(ticket, DateTimeOffset.UtcNow, default);
        await _helpdesk!.SaveChangesAsync();

        await service.UpdatePolicyAsync(
            created.Policy!.Id, Request("Tightened", 2, TicketPriority.Critical), Admin(), default);

        var sla = await _helpdesk.TicketSlas.AsNoTracking().SingleAsync(item => item.TicketId == ticket.Id);
        Assert.Equal(600, sla.ResolutionTargetMinutes);

        // The next ticket gets the new target, which is what editing a policy is for.
        var later = await AddTicketAsync();
        await service.StartAsync(later, DateTimeOffset.UtcNow, default);
        await _helpdesk.SaveChangesAsync();
        Assert.Equal(2, (await _helpdesk.TicketSlas.AsNoTracking()
            .SingleAsync(item => item.TicketId == later.Id)).ResolutionTargetMinutes);
    }

    /// <summary>FAILURE PATH: a policy a ticket has run against stays, so that clock stays explainable.</summary>
    [Fact]
    public async Task DeletePolicy_ThatATicketHasRunAgainst_IsRefused()
    {
        var service = Service();
        var created = await service.CreatePolicyAsync(Request("In use", 60), Admin(), default);
        var ticket = await AddTicketAsync();
        await service.StartAsync(ticket, DateTimeOffset.UtcNow, default);
        await _helpdesk!.SaveChangesAsync();

        Assert.Equal(SlaOutcome.InUse, await service.DeletePolicyAsync(created.Policy!.Id, Admin(), default));
    }

    [Fact]
    public async Task DeletePolicy_NothingHasRunAgainst_IsAllowed()
    {
        var service = Service();
        var created = await service.CreatePolicyAsync(Request("Unused", 60), Admin(), default);

        Assert.Equal(SlaOutcome.Success, await service.DeletePolicyAsync(created.Policy!.Id, Admin(), default));
    }

    /// <summary>FAILURE PATH: a condition naming something absent is refused, not silently ignored.</summary>
    [Fact]
    public async Task CreatePolicy_NamingACategoryThatDoesNotExist_IsRefused()
    {
        var result = await Service().CreatePolicyAsync(
            Request("Nonsense", 60, categoryId: Guid.CreateVersion7()), Admin(), default);

        Assert.Equal(SlaOutcome.CategoryNotFound, result.Outcome);
    }

    [Fact]
    public async Task DeleteCalendar_ThatAPolicyMeasuresAgainst_IsRefused()
    {
        var service = Service();
        await service.CreatePolicyAsync(Request("Uses the calendar", 60), Admin(), default);

        Assert.Equal(SlaOutcome.InUse, await service.DeleteCalendarAsync(_calendarId, Admin(), default));
    }

    /// <summary>Anything not named keeps its place after the ones that were, rather than reshuffling.</summary>
    [Fact]
    public async Task ReorderPolicies_RenumbersTheNamedOnesAndLeavesTheRestBehindThem()
    {
        var service = Service();
        var first = await service.CreatePolicyAsync(Request("First", 60, sortOrder: 0), Admin(), default);
        var second = await service.CreatePolicyAsync(Request("Second", 60, sortOrder: 1), Admin(), default);
        var third = await service.CreatePolicyAsync(Request("Third", 60, sortOrder: 2), Admin(), default);

        await service.ReorderPoliciesAsync([third.Policy!.Id, first.Policy!.Id], Admin(), default);

        var listed = await service.ListPoliciesAsync(default);
        // Third and First were named and are renumbered from zero; Second was not, and follows them.
        Assert.Equal(["Third", "First", "Second"], listed.Select(policy => policy.Name));
        Assert.Equal([0, 1, 2], listed.Select(policy => policy.SortOrder));
    }

    private sealed class NoOpRouter : INotificationRouter
    {
        public Task<NotificationRoutingReport> RouteAsync(
            NotificationEnvelope envelope,
            IReadOnlyCollection<string>? userIds,
            CancellationToken cancellationToken) =>
            Task.FromResult(new NotificationRoutingReport(0, 0, 0, 0));
    }

    private sealed class NoOpPublishEndpoint : IPublishEndpoint
    {
        public Task Publish<T>(T message, CancellationToken cancellationToken = default) where T : class =>
            Task.CompletedTask;
        public Task Publish<T>(T message, IPipe<PublishContext<T>> pipe, CancellationToken cancellationToken = default) where T : class =>
            Task.CompletedTask;
        public Task Publish<T>(T message, IPipe<PublishContext> pipe, CancellationToken cancellationToken = default) where T : class =>
            Task.CompletedTask;
        public Task Publish(object message, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Publish(object message, IPipe<PublishContext> pipe, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Publish(object message, Type messageType, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Publish(object message, Type messageType, IPipe<PublishContext> pipe, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Publish<T>(object values, CancellationToken cancellationToken = default) where T : class => Task.CompletedTask;
        public Task Publish<T>(object values, IPipe<PublishContext<T>> pipe, CancellationToken cancellationToken = default) where T : class => Task.CompletedTask;
        public Task Publish<T>(object values, IPipe<PublishContext> pipe, CancellationToken cancellationToken = default) where T : class => Task.CompletedTask;
        public ConnectHandle ConnectPublishObserver(IPublishObserver observer) => throw new NotSupportedException();
    }
}
