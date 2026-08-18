using Modules.Helpdesk.Data;
using Modules.Helpdesk.Features.Sla;

namespace Infrastructure.Tests;

/// <summary>
/// Which policy a ticket gets. Pure matching — no database — because the rule is the whole feature:
/// an ordered list where the first policy whose stated conditions hold is the one that applies.
/// </summary>
public sealed class SlaPolicyMatchingTests
{
    private static SlaPolicy Policy(
        TicketPriority? priority = null,
        TicketType? type = null,
        Guid? categoryId = null) => new()
    {
        Id = Guid.CreateVersion7(),
        Name = "Policy",
        Priority = priority,
        TicketType = type,
        CategoryId = categoryId,
    };

    private static Ticket Ticket(
        TicketPriority priority = TicketPriority.High,
        TicketType type = TicketType.Incident,
        Guid? categoryId = null) => new()
    {
        Id = Guid.CreateVersion7(),
        Title = "A ticket",
        Description = "Something happened.",
        Type = type,
        Priority = priority,
        CategoryId = categoryId,
        RequesterId = "enduser1",
    };

    /// <summary>A policy that states no condition is the catch-all, and matches everything.</summary>
    [Fact]
    public void Matches_APolicyWithNoConditions_MatchesAnyTicket()
    {
        Assert.True(SlaService.Matches(Policy(), Ticket()));
        Assert.True(SlaService.Matches(Policy(), Ticket(TicketPriority.Low, TicketType.ServiceRequest)));
    }

    [Fact]
    public void Matches_OnPriority()
    {
        Assert.True(SlaService.Matches(Policy(priority: TicketPriority.High), Ticket(TicketPriority.High)));
        Assert.False(SlaService.Matches(Policy(priority: TicketPriority.High), Ticket(TicketPriority.Low)));
    }

    /// <summary>Incidents and service requests routinely carry different targets.</summary>
    [Fact]
    public void Matches_OnTicketType()
    {
        var policy = Policy(type: TicketType.ServiceRequest);
        Assert.True(SlaService.Matches(policy, Ticket(type: TicketType.ServiceRequest)));
        Assert.False(SlaService.Matches(policy, Ticket(type: TicketType.Incident)));
    }

    /// <summary>By id, so renaming a category cannot silently detach the SLA written for it.</summary>
    [Fact]
    public void Matches_OnCategory()
    {
        var category = Guid.CreateVersion7();
        Assert.True(SlaService.Matches(Policy(categoryId: category), Ticket(categoryId: category)));
        Assert.False(SlaService.Matches(Policy(categoryId: category), Ticket(categoryId: Guid.CreateVersion7())));
        // A ticket with no category does not match a policy that names one.
        Assert.False(SlaService.Matches(Policy(categoryId: category), Ticket()));
    }

    /// <summary>Conditions are AND-ed: every one the policy states has to hold.</summary>
    [Fact]
    public void Matches_EveryStatedConditionMustHold()
    {
        var category = Guid.CreateVersion7();
        var policy = Policy(TicketPriority.Critical, TicketType.Incident, category);

        Assert.True(SlaService.Matches(policy, Ticket(TicketPriority.Critical, TicketType.Incident, category)));
        Assert.False(SlaService.Matches(policy, Ticket(TicketPriority.High, TicketType.Incident, category)));
        Assert.False(SlaService.Matches(policy, Ticket(TicketPriority.Critical, TicketType.ServiceRequest, category)));
        Assert.False(SlaService.Matches(policy, Ticket(TicketPriority.Critical, TicketType.Incident)));
    }

    /// <summary>
    /// The behaviour the ordering exists for: a narrow rule placed above the catch-all wins, and the
    /// same rule placed below it never runs. This is why order is a column an administrator sets.
    /// </summary>
    [Fact]
    public void FirstMatchInOrderWins()
    {
        var category = Guid.CreateVersion7();
        var specific = Policy(TicketPriority.Critical, categoryId: category);
        specific.Name = "Critical network";
        var catchAll = Policy();
        catchAll.Name = "Everything else";
        var ticket = Ticket(TicketPriority.Critical, categoryId: category);

        SlaPolicy? Pick(params SlaPolicy[] ordered) =>
            ordered.FirstOrDefault(policy => SlaService.Matches(policy, ticket));

        Assert.Equal("Critical network", Pick(specific, catchAll)?.Name);
        Assert.Equal("Everything else", Pick(catchAll, specific)?.Name);
    }
}
