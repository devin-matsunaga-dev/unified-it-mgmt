using System.Security.Claims;
using System.Text.Encodings.Web;

using Contracts.Events;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Modules.Assets.Data;
using Modules.Helpdesk.Data;
using Modules.Helpdesk.Features.AlertTickets;
using Modules.Helpdesk.Features.TicketCis;
using Modules.Helpdesk.Features.Tickets;

using Platform.Data;
using Platform.Notifications;

using StackExchange.Redis;

namespace Infrastructure.Tests;

/// <summary>
/// The WP-3.6 verification list against the real thing: alerts driven through the automation with a
/// real Postgres holding the tickets and the dedupe rows, and a real Redis holding the rate limit and
/// the circuit breaker.
/// <para>
/// The consumers are deliberately not exercised through the broker here — the bus is off and the
/// automation is called directly, so an assertion cannot fail on delivery timing. What that leaves
/// unproved is the binding itself, which <c>AlertTicketBusIntegrationTests</c> covers.
/// </para>
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class AlertTicketAutomationIntegrationTests : IAsyncLifetime
{
    private const int BreakerThreshold = 5;
    private const int RateLimit = 2;

    private readonly AlertTicketApplication _application;
    private readonly string _redisConnectionString;

    public AlertTicketAutomationIntegrationTests(InfrastructureFixture infrastructure)
    {
        _redisConnectionString = infrastructure.RedisConnectionString;
        _application = new AlertTicketApplication(
            infrastructure.PostgresConnectionString,
            infrastructure.RabbitMqConnectionString,
            infrastructure.RedisConnectionString,
            infrastructure.MinioConnectionString);
    }

    public async Task InitializeAsync()
    {
        _ = _application.CreateClient();
        await using var scope = _application.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<PlatformDbContext>().Database.MigrateAsync();
        await scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>().Database.MigrateAsync();
        // WP-3.7 reads the CMDB through the Assets port on every raise, so the assets schema has to
        // exist here — not only in whichever other test in this collection happened to run first.
        await scope.ServiceProvider.GetRequiredService<AssetsDbContext>().Database.MigrateAsync();

        // The breaker and its window are global keys by design — a storm is global. They are reset
        // between tests by name, never with a FLUSHALL, which would take the alert-engine tests'
        // state with it (the WP-3.2 shared-fixture trap).
        await using var connection = await ConnectionMultiplexer.ConnectAsync(_redisConnectionString);
        await connection.GetDatabase().KeyDeleteAsync(
            [RedisAlertAutomationGuard.BreakerKey, RedisAlertAutomationGuard.WindowKey]);
        Recorder.Sent.Clear();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task HelpdeskMigrations_CurrentModel_HasNoPendingChanges()
    {
        await using var scope = _application.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();

        Assert.False(context.Database.HasPendingModelChanges());
    }

    // ---- the WP's verification list ----

    /// <summary>The WP's first step: raise an alert, get one ticket, and get it at the right priority.</summary>
    [Fact]
    public async Task Raise_ACriticalAlert_OpensOneTicketAtCriticalPriority()
    {
        var rule = NewRule();

        await RaiseAsync(rule);

        var entry = await EntryAsync(rule);
        Assert.NotNull(entry.TicketId);
        Assert.Equal(1, entry.OccurrenceCount);
        Assert.Equal(1, entry.TicketCount);
        Assert.Equal(0, entry.SuppressedCount);

        var ticket = await TicketAsync(entry.TicketId!.Value);
        Assert.Equal(TicketPriority.Critical, ticket.Priority);
        Assert.Equal(TicketType.Incident, ticket.Type);
        Assert.Equal("New", ticket.Status.Name);
        Assert.Equal("system:monitoring", ticket.RequesterId);
        Assert.Contains(rule.RuleId, ticket.Description, StringComparison.Ordinal);
    }

    /// <summary>
    /// The WP's second step, and the reason the dedupe key exists: the same alert ten times is one
    /// ticket with ten occurrences recorded on it, not ten tickets.
    /// </summary>
    [Fact]
    public async Task Raise_TheSameAlertTenTimes_KeepsOneTicketAndAnnotatesIt()
    {
        var rule = NewRule();

        for (var occurrence = 0; occurrence < 10; occurrence++)
        {
            await RaiseAsync(rule);
        }

        var entry = await EntryAsync(rule);
        Assert.Equal(10, entry.OccurrenceCount);
        Assert.Equal(1, entry.TicketCount);
        Assert.Single(await TicketsForRuleAsync(rule));

        // Nine annotations for nine repeats, all internal: a requester must never be mailed once a
        // cycle about an alert they did not raise.
        var comments = await CommentsAsync(entry.TicketId!.Value);
        Assert.Equal(9, comments.Count);
        Assert.All(comments, comment => Assert.True(comment.IsInternal));
        Assert.Contains(comments, comment =>
            comment.Body.Contains("Occurrence 10", StringComparison.Ordinal));
    }

    /// <summary>An escalation is news, and the note says which way it went rather than repeating itself.</summary>
    [Fact]
    public async Task Raise_AtAHigherSeverity_AnnotatesTheEscalationOnTheSameTicket()
    {
        var rule = NewRule();

        await RaiseAsync(rule, severity: "Warning");
        await RaiseAsync(rule, severity: "Critical");

        var entry = await EntryAsync(rule);
        Assert.Equal("Critical", entry.LastSeverity);
        Assert.Single(await TicketsForRuleAsync(rule));
        var comment = Assert.Single(await CommentsAsync(entry.TicketId!.Value));
        Assert.Contains("escalated from Warning to Critical", comment.Body, StringComparison.Ordinal);
    }

    /// <summary>
    /// The WP's third step. Auto-resolve is four guarded transitions from New, not one write — the
    /// history proves the automation walked the same chain an agent would have.
    /// </summary>
    [Fact]
    public async Task Clear_AnAlertWithATicket_AutoResolvesItWithANoteAndAFullTransitionHistory()
    {
        var rule = NewRule();
        await RaiseAsync(rule);
        var entry = await EntryAsync(rule);

        await ClearAsync(rule);

        var ticket = await TicketAsync(entry.TicketId!.Value);
        Assert.Equal("Resolved", ticket.Status.Name);

        var resolved = await EntryAsync(rule);
        Assert.NotNull(resolved.AutoResolvedAt);
        Assert.NotNull(resolved.LastClearedAt);

        await using var scope = _application.Services.CreateAsyncScope();
        var history = await scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>()
            .TicketTransitionHistory.Include(item => item.ToStatus)
            .Where(item => item.TicketId == ticket.Id)
            .OrderBy(item => item.OccurredAt).ThenBy(item => item.Id)
            .ToListAsync();
        Assert.Equal(
            ["Triage", "InProgress", "Pending", "Resolved"],
            history.Select(item => item.ToStatus.Name));
        Assert.All(history, item => Assert.Equal("system:monitoring", item.ActorId));
        Assert.Contains("cleared this alert automatically", history[^1].ResolutionNote!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The WP's fourth step: fifty distinct alerts. Each one passes its own per-rule rate limit — that
    /// is exactly why the breaker is global — so without it there would be fifty tickets.
    /// </summary>
    [Fact]
    public async Task Raise_AStormOfFiftyDistinctAlerts_TripsTheBreakerNotifiesAnAdminAndOpensNoFlood()
    {
        var rules = Enumerable.Range(0, 50).Select(_ => NewRule()).ToList();

        foreach (var rule in rules)
        {
            await RaiseAsync(rule);
        }

        await using var scope = _application.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
        var keys = rules.Select(rule => AlertTicketPolicy.DedupeKey(rule.DeviceId, rule.RuleId)).ToList();
        var entries = await context.AlertTickets.Where(entry => keys.Contains(entry.DedupeKey)).ToListAsync();

        Assert.Equal(50, entries.Count);
        Assert.Equal(BreakerThreshold, entries.Count(entry => entry.TicketId is not null));
        // Nothing is lost: every refused raise still has a row saying it happened and was suppressed.
        Assert.Equal(50 - BreakerThreshold, entries.Count(entry => entry.SuppressedCount == 1));

        var notice = Assert.Single(Recorder.Sent, message =>
            message.Template.Name == "AlertTicketBreakerTripped");
        Assert.Equal("it-admin@it-platform.test", notice.Recipient);
        Assert.Contains("circuit breaker tripped", notice.Template.Body, StringComparison.Ordinal);
    }

    /// <summary>
    /// The per-rule limit, which bites where the dedupe row cannot: a rule whose ticket keeps being
    /// auto-resolved would otherwise open a new one every cycle it flaps.
    /// </summary>
    [Fact]
    public async Task Raise_AfterEachTicketIsResolved_StopsAtThePerRuleRateLimit()
    {
        var rule = NewRule();

        for (var round = 0; round < RateLimit + 3; round++)
        {
            await RaiseAsync(rule);
            await ClearAsync(rule);
        }

        var entry = await EntryAsync(rule);
        Assert.Equal(RateLimit, entry.TicketCount);
        Assert.Equal(RateLimit + 3, entry.OccurrenceCount);
        Assert.Equal(3, entry.SuppressedCount);
        Assert.Equal(RateLimit, (await TicketsForRuleAsync(rule)).Count);
    }

    /// <summary>
    /// A rule that recurs after its ticket was finished gets a new one, because the WP-1.2 graph has
    /// no edge out of Resolved. The two are joined by a note in each direction — a second ticket that
    /// does not say why it exists reads as the duplicate this package is supposed to prevent.
    /// </summary>
    [Fact]
    public async Task Raise_AfterTheTicketWasResolved_OpensASuccessorAndLinksTheTwoByNote()
    {
        var rule = NewRule();
        await RaiseAsync(rule);
        var first = (await EntryAsync(rule)).TicketId!.Value;
        await ClearAsync(rule);

        await RaiseAsync(rule);

        var second = (await EntryAsync(rule)).TicketId!.Value;
        Assert.NotEqual(first, second);

        var firstNumber = (await TicketAsync(first)).Number;
        var secondNumber = (await TicketAsync(second)).Number;
        Assert.Contains(await CommentsAsync(first), comment =>
            comment.Body.Contains(secondNumber, StringComparison.Ordinal)
            && comment.Body.Contains("cannot be reopened", StringComparison.Ordinal));
        Assert.Contains(await CommentsAsync(second), comment =>
            comment.Body.Contains(firstNumber, StringComparison.Ordinal));
    }

    // ---- WP-3.7: CMDB context and the CI link ----

    /// <summary>
    /// The WP's own verification, end to end through the real ports: the automated ticket names the
    /// asset's owner, location and warranty, and it is <em>linked</em> to the CI — which is what makes
    /// the asset visible from the ticket and the ticket visible from the asset.
    /// </summary>
    [Fact]
    public async Task Raise_ForAnAlertOnAKnownCi_LinksTheCiAndCarriesItsOwnerLocationAndWarranty()
    {
        var ciId = await SeedCiAsync("core-sw-01", warrantyInDays: 12);
        var rule = NewRule(ciId);

        await RaiseAsync(rule);

        var ticketId = (await EntryAsync(rule)).TicketId!.Value;
        var ticket = await TicketAsync(ticketId);
        Assert.Contains("Owner: Dana Whitfield", ticket.Description, StringComparison.Ordinal);
        Assert.Contains("Location: Primary Data Centre", ticket.Description, StringComparison.Ordinal);
        Assert.Contains("Warranty: ExpiringSoon", ticket.Description, StringComparison.Ordinal);
        Assert.Contains("core-sw-01", ticket.Description, StringComparison.Ordinal);

        await using var scope = _application.Services.CreateAsyncScope();
        var link = Assert.Single(await scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>()
            .TicketCiLinks.Where(item => item.TicketId == ticketId).ToListAsync());
        Assert.Equal(ciId, link.CiId);
        Assert.Equal("system:monitoring", link.LinkedById);
    }

    /// <summary>
    /// The link is a real one, made through the same service an agent's "Link asset" button calls — so
    /// the ticket's own card resolves the CI live, with the enrichment on it, rather than the
    /// automation writing a row nothing renders.
    /// </summary>
    [Fact]
    public async Task Raise_ForAnAlertOnAKnownCi_MakesTheAssetCardResolveWithWarrantyAndRelatedTickets()
    {
        var ciId = await SeedCiAsync("edge-rtr-02", warrantyInDays: -40);
        var existing = await OpenATicketLinkedToAsync(ciId, "Port flapping on the uplink");
        var rule = NewRule(ciId);

        await RaiseAsync(rule);

        var ticketId = (await EntryAsync(rule)).TicketId!.Value;
        await using var scope = _application.Services.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<ITicketCiLinkService>()
            .ListAsync(ticketId, Agent, CancellationToken.None);
        var card = Assert.Single(result.Links!);

        Assert.Equal("Dana Whitfield", card.OwnerName);
        Assert.Equal("Primary Data Centre", card.SiteName);
        Assert.Equal("Infrastructure", card.DepartmentName);
        Assert.Equal("Expired", card.WarrantyStatus);
        Assert.Equal(-40, card.WarrantyDaysRemaining);
        // The ticket that was already open about this CI, and never this ticket itself.
        var related = Assert.Single(card.OpenRelatedTickets);
        Assert.Equal(existing, related.TicketId);
        Assert.DoesNotContain(card.OpenRelatedTickets, item => item.TicketId == ticketId);
    }

    /// <summary>
    /// The description is a dated record, so the tickets it names are the ones that were open when the
    /// alert fired — including the one an agent had already raised by hand.
    /// </summary>
    [Fact]
    public async Task Raise_WhenTheCiAlreadyHasAnOpenTicket_NamesItInTheDescription()
    {
        var ciId = await SeedCiAsync("dist-sw-07", warrantyInDays: 400);
        var existing = await OpenATicketLinkedToAsync(ciId, "Fan noise reported on site");
        var existingNumber = (await TicketAsync(existing)).Number;
        var rule = NewRule(ciId);

        await RaiseAsync(rule);

        var ticket = await TicketAsync((await EntryAsync(rule)).TicketId!.Value);
        Assert.Contains($"Open related tickets: {existingNumber}", ticket.Description, StringComparison.Ordinal);
    }

    // ---- failure paths ----

    /// <summary>
    /// Nothing stops a monitored device's CI being deleted (the WP-3.1 note about the missing
    /// <c>IMonitoredDeviceDirectory</c> port). The alert still has to become a ticket: a degraded
    /// ticket is recoverable, a dropped alert is not.
    /// </summary>
    [Fact]
    public async Task Raise_ForAnAlertWhoseCiIsNotInTheCmdb_StillOpensTheTicketAndLinksNothing()
    {
        var rule = NewRule();

        await RaiseAsync(rule);

        var ticketId = (await EntryAsync(rule)).TicketId!.Value;
        var ticket = await TicketAsync(ticketId);
        Assert.Contains("not found in the CMDB", ticket.Description, StringComparison.Ordinal);

        await using var scope = _application.Services.CreateAsyncScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>()
            .TicketCiLinks.Where(item => item.TicketId == ticketId).ToListAsync());
    }


    /// <summary>
    /// A clear for a rule this platform never ticketed — the automation was off, or the raise was
    /// suppressed. A fact about the estate rather than a fault, so it must not fault the message and
    /// send an alert clear to the error queue forever.
    /// </summary>
    [Fact]
    public async Task Clear_ForARuleWithNoTicket_IsRecordedWithoutFailing()
    {
        var rule = NewRule();

        await ClearAsync(rule);

        await using var scope = _application.Services.CreateAsyncScope();
        var key = AlertTicketPolicy.DedupeKey(rule.DeviceId, rule.RuleId);
        Assert.Null(await scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>()
            .AlertTickets.SingleOrDefaultAsync(entry => entry.DedupeKey == key));
    }

    /// <summary>
    /// An agent got there first. The clear still lands as a note — "monitoring agrees this is over" is
    /// worth having — but nothing re-transitions a ticket somebody has already finished.
    /// </summary>
    [Fact]
    public async Task Clear_AfterAnAgentResolvedItByHand_AnnotatesRatherThanTransitioning()
    {
        var rule = NewRule();
        await RaiseAsync(rule);
        var ticketId = (await EntryAsync(rule)).TicketId!.Value;
        await CloseByHandAsync(ticketId);

        await ClearAsync(rule);

        var ticket = await TicketAsync(ticketId);
        Assert.Equal("Closed", ticket.Status.Name);
        Assert.Contains(await CommentsAsync(ticketId), comment =>
            comment.Body.Contains("cleared this alert automatically", StringComparison.Ordinal));
        // The row records the clear even though it changed no status.
        Assert.NotNull((await EntryAsync(rule)).LastClearedAt);
        Assert.Null((await EntryAsync(rule)).AutoResolvedAt);
    }

    // ---- WP-5.6: an auto-remediation result reaching the ticket ----

    /// <summary>
    /// The WP's first verification step on the Helpdesk side: the runbook's output lands on the ticket
    /// the alert opened, as an internal note.
    /// </summary>
    [Fact]
    public async Task RunbookResult_ForARuleWithATicket_IsRecordedOnIt()
    {
        var rule = NewRule();
        await RaiseAsync(rule);
        var ticketId = (await EntryAsync(rule)).TicketId!.Value;

        await RecordRunbookAsync(rule, "Succeeded", exitCode: 0, output: "Restarted nginx.");

        var comment = Assert.Single(
            await CommentsAsync(ticketId),
            item => item.Body.Contains("ran successfully", StringComparison.Ordinal));
        Assert.Contains("Restarted nginx.", comment.Body, StringComparison.Ordinal);
        Assert.True(comment.IsInternal);
        // The ticket is not touched otherwise: a remediation that worked is not a resolution, and
        // only the alert clearing resolves this ticket (WP-3.6).
        Assert.Equal("New", (await TicketAsync(ticketId)).Status.Name);
    }

    /// <summary>
    /// The WP's third verification step: a failure escalates and says, on the ticket, that nothing
    /// will try again — which is the sentence a technician reads before deciding whether to wait.
    /// </summary>
    [Fact]
    public async Task RunbookResult_AFailureOnARuleWithATicket_SaysNothingWillRetry()
    {
        var rule = NewRule();
        await RaiseAsync(rule);
        var ticketId = (await EntryAsync(rule)).TicketId!.Value;

        await RecordRunbookAsync(rule, "Failed", exitCode: 1, error: "Unit nginx.service not found.");

        Assert.Contains(
            await CommentsAsync(ticketId),
            comment => comment.Body.Contains("Nothing was retried and nothing will be", StringComparison.Ordinal));
    }

    /// <summary>
    /// The escalation with nowhere to land. The alert was never ticketed — suppressed, or automation
    /// off at the time — so the failure opens a ticket of its own rather than disappearing.
    /// </summary>
    [Fact]
    public async Task RunbookResult_AFailureWithNoTicket_OpensOneAtHighPriority()
    {
        var rule = NewRule();

        await RecordRunbookAsync(rule, "Failed", exitCode: 1, error: "Permission denied.");

        await using var scope = _application.Services.CreateAsyncScope();
        var ticket = await scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>()
            .Tickets.Include(item => item.Status)
            .SingleAsync(item => item.Description.Contains(rule.RuleId)
                && item.Title.StartsWith("[Remediation failed]"));
        Assert.Contains("Permission denied.", ticket.Description, StringComparison.Ordinal);
        Assert.Equal(TicketLevel.High, ticket.Urgency);
    }

    /// <summary>
    /// The other half of that rule, and the one that keeps the ticket queue habitable: a remediation
    /// that <em>worked</em> and had no ticket opens none. The execution row and the audit entry are
    /// its record, and a ticket saying "nothing needs doing" is one somebody has to close.
    /// </summary>
    [Fact]
    public async Task RunbookResult_ASuccessWithNoTicket_OpensNothing()
    {
        var rule = NewRule();

        await RecordRunbookAsync(rule, "Succeeded", exitCode: 0, output: "Restarted nginx.");

        await using var scope = _application.Services.CreateAsyncScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>()
            .Tickets.Where(item => item.Description.Contains(rule.RuleId)).ToListAsync());
    }

    /// <summary>
    /// A failed run somebody started by hand still escalates. Their request returned long before the
    /// result arrived, so there is otherwise nothing at all to notice it.
    /// </summary>
    [Fact]
    public async Task RunbookResult_AFailedManualRun_StillOpensATicket()
    {
        var rule = NewRule();

        await RecordRunbookAsync(rule, "Failed", exitCode: 1, error: "No such unit.", isManual: true);

        await using var scope = _application.Services.CreateAsyncScope();
        // Matched on the device rather than on the title alone: a manual run's ticket names no rule,
        // and the whole collection shares one database — so anything less specific than a per-test id
        // finds every previous run's ticket too. The trap WP-3.4's notes record.
        var ticket = await scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>()
            .Tickets.SingleAsync(item => item.Title.StartsWith("[Remediation failed]")
                && item.Description.Contains(rule.DeviceId.ToString()));
        Assert.Contains("run by hand", ticket.Description, StringComparison.Ordinal);
        Assert.Contains("No such unit.", ticket.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunbookResult_WithNoExecution_Throws()
    {
        await using var scope = _application.Services.CreateAsyncScope();
        var automation = scope.ServiceProvider.GetRequiredService<IAlertTicketAutomation>();

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            automation.RecordRunbookResultAsync(null!, CancellationToken.None));
    }

    [Fact]
    public async Task Raise_WithNoAlert_Throws()
    {
        await using var scope = _application.Services.CreateAsyncScope();
        var automation = scope.ServiceProvider.GetRequiredService<IAlertTicketAutomation>();

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            automation.RaiseAsync(null!, CancellationToken.None));
    }

    // ---- driving ----

    private sealed record RuleFixture(Guid DeviceId, Guid CiId, Guid CheckId, string RuleId);

    /// <summary>
    /// A rule on a device nobody has a CI for, unless one is named. The default is deliberate: most of
    /// this class is about the ticket rather than the CMDB, and a random CI id is exactly the
    /// "CI not found" case the enrichment has to survive.
    /// </summary>
    private static RuleFixture NewRule(Guid? ciId = null)
    {
        var checkId = Guid.CreateVersion7();
        return new RuleFixture(
            Guid.CreateVersion7(),
            ciId ?? Guid.CreateVersion7(),
            checkId,
            $"check:{checkId}:cpu.utilisation_percent");
    }

    /// <summary>
    /// A CI written straight through the Assets context, following WP-2.8's seeder: this is a fixture,
    /// not an operator's edit, and routing it through <c>ICiService</c> would audit and publish it.
    /// </summary>
    private async Task<Guid> SeedCiAsync(string name, int warrantyInDays)
    {
        await using var scope = _application.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AssetsDbContext>();
        var now = DateTimeOffset.UtcNow;
        var ci = new NetworkDeviceCi
        {
            Id = Guid.CreateVersion7(),
            Name = $"{name}-{Guid.CreateVersion7():N}"[..24],
            AssetTag = $"AST-{Random.Shared.Next(100_000, 999_999)}",
            LifecycleState = CiLifecycleState.Deployed,
            OwnerName = "Dana Whitfield",
            SiteName = "Primary Data Centre",
            DepartmentName = "Infrastructure",
            WarrantyExpiresAt = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(warrantyInDays),
            ManagementIp = "10.20.30.40",
            Vendor = "Cisco",
            PortCount = 48,
            CreatedAt = now,
            UpdatedAt = now,
        };
        context.Cis.Add(ci);
        await context.SaveChangesAsync();
        return ci.Id;
    }

    /// <summary>A ticket an agent raised by hand and linked to the CI, so "already being worked on" is real.</summary>
    private async Task<Guid> OpenATicketLinkedToAsync(Guid ciId, string title)
    {
        await using var scope = _application.Services.CreateAsyncScope();
        var created = await scope.ServiceProvider.GetRequiredService<ITicketService>().CreateAsync(
            new CreateTicketRequest(
                title, "Raised by an agent before monitoring noticed.", TicketType.Incident,
                TicketLevel.Medium, TicketLevel.Medium, RequesterId: null, QueueId: null),
            Agent,
            CancellationToken.None);
        Assert.Equal(TicketWriteOutcome.Success, created.Outcome);

        var link = await scope.ServiceProvider.GetRequiredService<ITicketCiLinkService>().LinkAsync(
            created.Ticket!.Id, new LinkTicketCiRequest(ciId), Agent, CancellationToken.None);
        Assert.Equal(TicketCiLinkOutcome.Success, link.Outcome);
        return created.Ticket.Id;
    }

    private async Task RaiseAsync(RuleFixture rule, string severity = "Critical")
    {
        await using var scope = _application.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IAlertTicketAutomation>().RaiseAsync(
            new AlertRaised(
                Guid.CreateVersion7(), DateTimeOffset.UtcNow, Guid.CreateVersion7(),
                rule.DeviceId, rule.CiId, rule.CheckId, rule.RuleId, "SNMP: CPU", severity,
                "cpu.utilisation_percent", 97.5, 90, "CPU utilisation is above the critical threshold.",
                DateTimeOffset.UtcNow, 3),
            CancellationToken.None);
    }

    private async Task RecordRunbookAsync(
        RuleFixture rule,
        string outcome,
        int? exitCode,
        string? output = null,
        string? error = null,
        bool isManual = false)
    {
        await using var scope = _application.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IAlertTicketAutomation>()
            .RecordRunbookResultAsync(
                new RunbookExecutionCompleted(
                    Guid.CreateVersion7(), DateTimeOffset.UtcNow, Guid.CreateVersion7(),
                    Guid.CreateVersion7(), "restart-service", "Restart a service", 1,
                    isManual ? null : Guid.CreateVersion7(),
                    rule.DeviceId, rule.CiId,
                    isManual ? null : rule.RuleId,
                    outcome, exitCode, output, error,
                    isManual ? "technician1" : "system:monitoring", "poller-1",
                    DateTimeOffset.UtcNow.AddSeconds(-8), DateTimeOffset.UtcNow, 8),
                CancellationToken.None);
    }

    private async Task ClearAsync(RuleFixture rule)
    {
        await using var scope = _application.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IAlertTicketAutomation>().ClearAsync(
            new AlertCleared(
                Guid.CreateVersion7(), DateTimeOffset.UtcNow, Guid.CreateVersion7(),
                rule.DeviceId, rule.CiId, rule.CheckId, rule.RuleId, "SNMP: CPU", "Critical",
                "cpu.utilisation_percent", 11.5, "CPU utilisation is back below the threshold.",
                DateTimeOffset.UtcNow.AddMinutes(-5), 300),
            CancellationToken.None);
    }

    /// <summary>Walks a ticket all the way to Closed as an agent would, so the clear finds it finished.</summary>
    private async Task CloseByHandAsync(Guid ticketId)
    {
        await using var scope = _application.Services.CreateAsyncScope();
        var service = scope.ServiceProvider
            .GetRequiredService<Modules.Helpdesk.Features.Tickets.ITicketService>();
        foreach (var status in new[] { "Triage", "InProgress", "Pending", "Resolved", "Closed" })
        {
            var result = await service.TransitionAsync(
                ticketId,
                new Modules.Helpdesk.Features.Tickets.TransitionTicketRequest(status, "Handled by an agent."),
                Agent,
                CancellationToken.None);
            Assert.Equal(
                Modules.Helpdesk.Features.Tickets.TransitionTicketOutcome.Success, result.Outcome);
        }
    }

    private static readonly ClaimsPrincipal Agent = new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, "technician1"),
            new Claim(ClaimTypes.Role, "Technician"),
        ],
        "Test"));

    // ---- reading back ----

    private async Task<AlertTicket> EntryAsync(RuleFixture rule)
    {
        await using var scope = _application.Services.CreateAsyncScope();
        var key = AlertTicketPolicy.DedupeKey(rule.DeviceId, rule.RuleId);
        return await scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>()
            .AlertTickets.SingleAsync(entry => entry.DedupeKey == key);
    }

    private async Task<Ticket> TicketAsync(Guid ticketId)
    {
        await using var scope = _application.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>()
            .Tickets.Include(ticket => ticket.Status).SingleAsync(ticket => ticket.Id == ticketId);
    }

    /// <summary>
    /// Every ticket this rule has ever opened, found by the rule id in the description. Deliberately
    /// not a count of all tickets: the whole collection shares one database, so a global count would
    /// pass or fail on test order — the trap WP-3.4's notes record.
    /// </summary>
    private async Task<IReadOnlyList<Ticket>> TicketsForRuleAsync(RuleFixture rule)
    {
        await using var scope = _application.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>()
            .Tickets.Where(ticket => ticket.Description.Contains(rule.RuleId))
            .OrderBy(ticket => ticket.CreatedAt).ToListAsync();
    }

    private async Task<IReadOnlyList<TicketComment>> CommentsAsync(Guid ticketId)
    {
        await using var scope = _application.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>()
            .TicketComments.Where(comment => comment.TicketId == ticketId)
            .OrderBy(comment => comment.CreatedAt).ThenBy(comment => comment.Id).ToListAsync();
    }

    // ---- host ----

    /// <summary>
    /// Captures notifications instead of mailing them. The admin notice is the only externally visible
    /// half of the circuit breaker, so "the admin was told" has to be assertable rather than a log line.
    /// </summary>
    private sealed class RecordingNotificationService : INotificationService
    {
        public Task SendAsync(NotificationMessage message, CancellationToken cancellationToken = default)
        {
            Recorder.Sent.Add(message);
            return Task.CompletedTask;
        }
    }

    private static class Recorder
    {
        public static readonly List<NotificationMessage> Sent = [];
    }

    private sealed class AlertTicketApplication : WebApplicationFactory<Program>
    {
        private readonly string _connectionString;
        private readonly string _rabbitMqConnectionString;
        private readonly string _redisConnectionString;
        private readonly string _minioConnectionString;

        public AlertTicketApplication(
            string connectionString,
            string rabbitMqConnectionString,
            string redisConnectionString,
            string minioConnectionString)
        {
            _connectionString = connectionString;
            _rabbitMqConnectionString = rabbitMqConnectionString;
            _redisConnectionString = redisConnectionString;
            _minioConnectionString = minioConnectionString;
            // Aspire's AddNpgsqlDataSource and AddRedisClient read the builder's configuration while
            // the host is being built — before WebApplicationFactory's sources exist. Same reason as
            // WP-3.4's and WP-3.5's test hosts.
            Environment.SetEnvironmentVariable("ConnectionStrings__database", connectionString);
            Environment.SetEnvironmentVariable("ConnectionStrings__redis", redisConnectionString);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Authentication:Authority"] = "https://identity.example.test/realms/it-platform",
                    ["Authentication:Audience"] = "it-platform-api",
                    ["Authentication:ClientId"] = "it-platform-web",
                    ["Authentication:PostLogoutRedirectUri"] = "https://app.example.test/",
                    ["ConnectionStrings:database"] = _connectionString,
                    ["ConnectionStrings:rabbitmq"] = _rabbitMqConnectionString,
                    ["ConnectionStrings:redis"] = _redisConnectionString,
                    ["ConnectionStrings:minio"] = _minioConnectionString,
                    ["ObjectStorage:AccessKey"] = "minioadmin",
                    ["ObjectStorage:SecretKey"] = "minio-test-password",
                    ["Platform:ApplyMigrations"] = "false",
                    ["Platform:EnableMessageBus"] = "false",
                    ["Platform:EnableScheduler"] = "false",
                    // Low enough that a storm is a handful of tickets rather than a hundred, and a
                    // window long enough that no test can outlast it by being slow.
                    [$"{AlertTicketOptions.SectionName}:BreakerThreshold"] = $"{BreakerThreshold}",
                    [$"{AlertTicketOptions.SectionName}:BreakerWindowSeconds"] = "600",
                    [$"{AlertTicketOptions.SectionName}:BreakerCooldownSeconds"] = "600",
                    [$"{AlertTicketOptions.SectionName}:RateLimitPerRulePerMinute"] = $"{RateLimit}",
                    [$"{AlertTicketOptions.SectionName}:AdminRecipient"] = "it-admin@it-platform.test",
                }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.RemoveAll<INotificationService>();
                services.AddScoped<INotificationService, RecordingNotificationService>();
                services.AddAuthentication(options =>
                    {
                        options.DefaultAuthenticateScheme = AlertTicketAuthenticationHandler.TestScheme;
                        options.DefaultChallengeScheme = AlertTicketAuthenticationHandler.TestScheme;
                        options.DefaultForbidScheme = AlertTicketAuthenticationHandler.TestScheme;
                    })
                    .AddScheme<AuthenticationSchemeOptions, AlertTicketAuthenticationHandler>(
                        AlertTicketAuthenticationHandler.TestScheme,
                        _ => { });
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            Environment.SetEnvironmentVariable("ConnectionStrings__database", null);
            Environment.SetEnvironmentVariable("ConnectionStrings__redis", null);
        }
    }

    private sealed class AlertTicketAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string TestScheme = "AlertTicketTest";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(AuthenticateResult.NoResult());
    }
}
