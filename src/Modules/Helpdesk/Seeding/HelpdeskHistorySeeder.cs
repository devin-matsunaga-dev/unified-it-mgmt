using Microsoft.EntityFrameworkCore;
using Modules.Helpdesk.Data;
using Modules.Helpdesk.Features.Tickets;

namespace Modules.Helpdesk.Seeding;

public sealed record HelpdeskHistorySeedResult(
    int CalendarsAdded,
    int PoliciesAdded,
    int TicketsAdded,
    int CommentsAdded,
    int WorklogsAdded,
    int TransitionsAdded,
    int SlasAdded);

/// <summary>
/// Seeds a lived-in helpdesk backlog: 200 tickets spread across every status, age band and SLA state,
/// with the comments, worklogs and history rows an agent would expect to find on them. The dev database is
/// recreated on most AppHost restarts, so this history has to be generated rather than accumulated by hand.
/// Everything is derived from a fixed random seed and deterministic ids, so re-running adds nothing.
/// </summary>
public sealed class HelpdeskHistorySeeder(HelpdeskDbContext dbContext)
{
    /// <summary>Tickets to generate. The number is fixed so the dataset is reproducible.</summary>
    public const int TicketCount = 200;

    private const int RandomSeed = 20260807;
    private static readonly Guid CalendarId = Guid.Parse("01980001-0000-7000-8000-000000000001");

    /// <summary>The workflow is linear (WP-1.2), so a ticket's history is always a prefix of this chain.</summary>
    private static readonly Guid[] StatusChain =
    [
        DefaultTicketStatuses.NewId,
        DefaultTicketStatuses.TriageId,
        DefaultTicketStatuses.InProgressId,
        DefaultTicketStatuses.PendingId,
        DefaultTicketStatuses.ResolvedId,
        DefaultTicketStatuses.ClosedId,
    ];

    /// <summary>Priority-only policies, matching how <see cref="Features.Sla.SlaService"/> looks a policy up.</summary>
    private static readonly (int Index, string Name, TicketPriority Priority, int ResponseMinutes, int ResolutionMinutes)[] Policies =
    [
        (1, "Critical priority", TicketPriority.Critical, 15, 240),
        (2, "High priority", TicketPriority.High, 30, 480),
        (3, "Medium priority", TicketPriority.Medium, 60, 1_440),
        (4, "Low priority", TicketPriority.Low, 120, 2_880),
    ];

    /// <summary>How many tickets sit in each status, and how old tickets in that status are (days).</summary>
    private static readonly (Guid StatusId, int Count, double MinAgeDays, double MaxAgeDays)[] StatusPlan =
    [
        (DefaultTicketStatuses.NewId, 16, 0.05, 3),
        (DefaultTicketStatuses.TriageId, 14, 0.5, 6),
        (DefaultTicketStatuses.InProgressId, 40, 1, 18),
        (DefaultTicketStatuses.PendingId, 20, 2, 25),
        (DefaultTicketStatuses.ResolvedId, 40, 4, 45),
        (DefaultTicketStatuses.ClosedId, 70, 20, 150),
    ];

    private static readonly (string Id, string DisplayName)[] Technicians =
    [
        ("technician1", "Technician One"),
        ("technician2", "Technician Two"),
        ("technician3", "Technician Three"),
        ("technician4", "Technician Four"),
    ];

    private static readonly (string Id, string DisplayName)[] Requesters =
    [
        ("enduser1", "End User One"),
        ("enduser2", "End User Two"),
        ("enduser3", "End User Three"),
        ("enduser4", "End User Four"),
        ("enduser5", "End User Five"),
        ("enduser6", "End User Six"),
        ("enduser7", "End User Seven"),
        ("enduser8", "End User Eight"),
        ("enduser9", "End User Nine"),
        ("enduser10", "End User Ten"),
    ];

    /// <summary>Ticket shapes per seeded category, so titles read like real service-desk traffic.</summary>
    private static readonly TicketTemplate[] Templates =
    [
        new("01980000-0000-7000-8000-000000000511", TicketType.Incident, "Laptop will not power on",
            "The laptop shows no lights when the power button is held. It was working when I shut it down yesterday evening."),
        new("01980000-0000-7000-8000-000000000511", TicketType.Incident, "Docking station stopped driving the second monitor",
            "Since this morning only one external monitor is detected through the dock. Swapping the cables makes no difference."),
        new("01980000-0000-7000-8000-000000000511", TicketType.ServiceRequest, "Replacement laptop for a starter",
            "A new starter joins on Monday and needs a standard build laptop with the finance software installed."),
        new("01980000-0000-7000-8000-000000000511", TicketType.Incident, "Battery drains within an hour",
            "The battery reports full charge but the machine shuts down after roughly one hour away from the desk."),
        new("01980000-0000-7000-8000-000000000512", TicketType.Incident, "Printer jams on every duplex job",
            "The floor printer jams half way through any double sided job. Single sided printing is fine."),
        new("01980000-0000-7000-8000-000000000512", TicketType.ServiceRequest, "Add the finance printer to my machine",
            "I have moved desks and can no longer see the finance printer in the print dialog."),
        new("01980000-0000-7000-8000-000000000512", TicketType.Incident, "Printer reports toner empty after a new cartridge",
            "A new toner cartridge was fitted this morning but the panel still reports the cartridge as empty."),
        new("01980000-0000-7000-8000-000000000521", TicketType.Incident, "Outlook keeps asking for a password",
            "Outlook prompts for credentials every few minutes. Webmail in the browser works without any prompt."),
        new("01980000-0000-7000-8000-000000000521", TicketType.Incident, "Calendar invitations are not reaching external guests",
            "Meeting invitations sent to customers never arrive. Internal recipients receive them immediately."),
        new("01980000-0000-7000-8000-000000000521", TicketType.ServiceRequest, "Shared mailbox access for the payroll team",
            "Please grant the three payroll staff send-as access to the payroll shared mailbox."),
        new("01980000-0000-7000-8000-000000000521", TicketType.Incident, "Mailbox is full and cannot receive messages",
            "Sending fails with a quota error and colleagues report bounce messages when writing to me."),
        new("01980000-0000-7000-8000-000000000522", TicketType.Incident, "Finance application times out at month end",
            "The reporting screen spins for two minutes and then returns a timeout. It only happens during month end close."),
        new("01980000-0000-7000-8000-000000000522", TicketType.ServiceRequest, "Install the CAD viewer on the workshop machines",
            "The workshop needs the read-only CAD viewer on the four shared machines before the next build review."),
        new("01980000-0000-7000-8000-000000000522", TicketType.Incident, "Reports export as an empty spreadsheet",
            "Exporting any report produces a spreadsheet with headers but no rows. Viewing the report on screen is fine."),
        new("01980000-0000-7000-8000-000000000531", TicketType.ServiceRequest, "Password reset after returning from leave",
            "My account password expired while I was on leave and I am now locked out of the laptop."),
        new("01980000-0000-7000-8000-000000000531", TicketType.Incident, "Account locked after too many attempts",
            "The account locked itself this morning even though the password had not changed."),
        new("01980000-0000-7000-8000-000000000532", TicketType.ServiceRequest, "Access to the operations reporting folder",
            "I have moved into the operations team and need read access to their reporting folder."),
        new("01980000-0000-7000-8000-000000000532", TicketType.ServiceRequest, "New starter account and group membership",
            "Please create an account for the new operations analyst with the standard operations group memberships."),
        new("01980000-0000-7000-8000-000000000504", TicketType.Incident, "Wireless drops every few minutes in the meeting rooms",
            "The wireless connection drops for around thirty seconds at a time in the first floor meeting rooms."),
        new("01980000-0000-7000-8000-000000000504", TicketType.Incident, "VPN disconnects when the laptop sleeps",
            "After the laptop wakes from sleep the VPN client reports an authentication failure and has to be restarted."),
        new("01980000-0000-7000-8000-000000000504", TicketType.Incident, "No network at the branch reception desk",
            "The reception desk network socket is dead. Moving to the neighbouring desk works."),
        new("01980000-0000-7000-8000-000000000504", TicketType.ServiceRequest, "Guest wireless for a customer visit",
            "We host a customer workshop next week and need guest wireless access for eight visitors."),
    ];

    /// <summary>Appended to each description so repeated templates still differ in the search index.</summary>
    private static readonly string[] Locations =
    [
        "Head Office, second floor",
        "Head Office, reception",
        "Primary Data Centre, operations room",
        "Regional Branch, open plan area",
        "Regional Branch, meeting room two",
    ];

    private static readonly string[] RequesterFollowUps =
    [
        "Any update on this? It is still blocking me this morning.",
        "I tried restarting as suggested but the problem came straight back.",
        "This has now started affecting a colleague on the same floor as well.",
        "Thanks for looking at it. I am at my desk all afternoon if you need access.",
        "The workaround is holding for now, but it is slow going.",
    ];

    private static readonly string[] AgentUpdates =
    [
        "Thanks for the detail. I have reproduced this and I am working through the logs now.",
        "I have applied a configuration change on our side. Could you sign out and back in, then let me know?",
        "This looks like a known issue with the current driver version. I am scheduling the update for you.",
        "I have raised this with the supplier and I will come back to you as soon as they respond.",
        "I will need about fifteen minutes on the machine. Would tomorrow morning suit you?",
    ];

    private static readonly string[] InternalNotes =
    [
        "Internal: third report from this floor this week. Worth checking the switch uplink.",
        "Internal: supplier case reference raised, waiting on their engineering team.",
        "Internal: user has a workaround, so this can wait behind the month end work.",
        "Internal: hardware is out of warranty, replacement will need manager approval.",
        "Internal: checked the audit log, nothing changed on the account before the failure.",
    ];

    private static readonly string[] WorklogNotes =
    [
        "Investigated logs and reproduced the fault.",
        "Remote session with the user.",
        "Applied the configuration change and verified.",
        "Bench testing the replacement hardware.",
        "Call with the supplier support engineer.",
    ];

    private static readonly string[] ResolutionNotes =
    [
        "Replaced the faulty hardware and confirmed normal operation with the user.",
        "Driver updated to the current release; the fault has not returned over two working days.",
        "Access granted and verified by the requester.",
        "Configuration corrected on the server side; user confirmed the problem is gone.",
        "Cleared the fault after the supplier firmware update.",
    ];

    public async Task<HelpdeskHistorySeedResult> SeedAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        var calendarsAdded = 0;
        if (!await dbContext.BusinessHoursCalendars.AnyAsync(calendar => calendar.Id == CalendarId, cancellationToken))
        {
            dbContext.BusinessHoursCalendars.Add(new BusinessHoursCalendar
            {
                Id = CalendarId,
                Name = "Standard business hours",
                TimeZoneId = "UTC",
                WorkingDays = BusinessDays.Weekdays,
                StartTime = new TimeOnly(9, 0),
                EndTime = new TimeOnly(17, 0),
                CreatedAt = now,
            });
            calendarsAdded = 1;
        }

        var policiesAdded = 0;
        var policyIds = new Dictionary<TicketPriority, Guid>();
        var policyTargets = new Dictionary<TicketPriority, (int Response, int Resolution)>();
        foreach (var (index, name, priority, responseMinutes, resolutionMinutes) in Policies)
        {
            var policyId = DeterministicId(PolicyKind, index);
            policyIds[priority] = policyId;
            policyTargets[priority] = (responseMinutes, resolutionMinutes);
            if (!await dbContext.SlaPolicies.AnyAsync(policy => policy.Id == policyId, cancellationToken))
            {
                dbContext.SlaPolicies.Add(new SlaPolicy
                {
                    Id = policyId,
                    Name = name,
                    Priority = priority,
                    Category = null,
                    ResponseTargetMinutes = responseMinutes,
                    ResolutionTargetMinutes = resolutionMinutes,
                    WarningPercent = 80,
                    CalendarId = CalendarId,
                    IsActive = true,
                    CreatedAt = now,
                });
                policiesAdded++;
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        var candidateIds = Enumerable.Range(0, TicketCount).Select(index => DeterministicId(TicketKind, index)).ToArray();
        var existingIds = await dbContext.Tickets.Where(ticket => candidateIds.Contains(ticket.Id))
            .Select(ticket => ticket.Id).ToHashSetAsync(cancellationToken);
        var queueId = await dbContext.TicketQueues.Where(queue => queue.Name == "Service Desk")
            .Select(queue => (Guid?)queue.Id).FirstOrDefaultAsync(cancellationToken);
        var assetTagFieldId = await dbContext.TicketCustomFields.Where(field => field.Key == "asset_tag")
            .Select(field => (Guid?)field.Id).FirstOrDefaultAsync(cancellationToken);

        var random = new Random(RandomSeed);
        var statuses = StatusPlan
            .SelectMany(entry => Enumerable.Repeat((entry.StatusId, entry.MinAgeDays, entry.MaxAgeDays), entry.Count))
            .ToArray();
        var counts = new SeedCounters();

        for (var index = 0; index < TicketCount; index++)
        {
            var (statusId, minAgeDays, maxAgeDays) = statuses[index];
            var template = Templates[index % Templates.Length];
            var requester = Requesters[index % Requesters.Length];
            var urgency = (TicketLevel)(1 + random.Next(3));
            var impact = (TicketLevel)(1 + random.Next(3));
            var priority = TicketPriorityMatrix.Calculate(urgency, impact);
            var createdAt = now - TimeSpan.FromDays(minAgeDays + (random.NextDouble() * (maxAgeDays - minAgeDays)));
            var ageSeed = random.Next();

            // The random sequence must advance identically whether or not this ticket already exists,
            // otherwise a partially seeded database would produce a different dataset on the next run.
            if (existingIds.Contains(candidateIds[index])) continue;

            var isOpen = statusId != DefaultTicketStatuses.ResolvedId && statusId != DefaultTicketStatuses.ClosedId;
            (string Id, string DisplayName)? assignedTechnician = statusId == DefaultTicketStatuses.NewId
                ? null
                : Technicians[index % Technicians.Length];
            var chainLength = Array.IndexOf(StatusChain, statusId);
            var lastActivityAt = createdAt + ((now - createdAt) * 0.85);

            var ticket = new Ticket
            {
                Id = candidateIds[index],
                Title = template.Title,
                Description = $"{template.Description}\n\nReported from {Locations[ageSeed % Locations.Length]}.",
                Type = template.Type,
                Urgency = urgency,
                Impact = impact,
                Priority = priority,
                StatusId = statusId,
                RequesterId = requester.Id,
                RequesterDisplayName = requester.DisplayName,
                RequesterEmail = $"{requester.Id}@example.test",
                QueueId = queueId,
                AssignedTechnicianId = assignedTechnician?.Id,
                CategoryId = Guid.Parse(template.CategoryId),
                CreatedAt = createdAt,
                UpdatedAt = chainLength == 0 ? createdAt : lastActivityAt,
            };
            dbContext.Tickets.Add(ticket);
            counts.Tickets++;

            if (assetTagFieldId is { } fieldId && template.CategoryId == LaptopCategoryId)
            {
                dbContext.TicketCustomFieldValues.Add(new TicketCustomFieldValue
                {
                    Id = DeterministicId(CustomFieldValueKind, index),
                    TicketId = ticket.Id,
                    FieldId = fieldId,
                    Value = $"LT-{1000 + index:0000}",
                    UpdatedAt = createdAt,
                });
            }

            for (var step = 1; step <= chainLength; step++)
            {
                var occurredAt = createdAt + ((lastActivityAt - createdAt) * step / (chainLength + 1d));
                var toStatusId = StatusChain[step];
                dbContext.TicketTransitionHistory.Add(new TicketTransitionHistory
                {
                    Id = DeterministicId(TransitionKind, index, step),
                    TicketId = ticket.Id,
                    FromStatusId = StatusChain[step - 1],
                    ToStatusId = toStatusId,
                    ResolutionNote = toStatusId == DefaultTicketStatuses.ResolvedId
                        ? ResolutionNotes[ageSeed % ResolutionNotes.Length]
                        : null,
                    ActorId = assignedTechnician?.Id ?? Technicians[0].Id,
                    OccurredAt = occurredAt,
                });
                counts.Transitions++;
            }

            if (assignedTechnician is { } technician && queueId is { } assignmentQueueId)
            {
                dbContext.TicketAssignmentHistory.Add(new TicketAssignmentHistory
                {
                    Id = DeterministicId(AssignmentHistoryKind, index),
                    TicketId = ticket.Id,
                    QueueId = assignmentQueueId,
                    FromTechnicianId = null,
                    ToTechnicianId = technician.Id,
                    Kind = AssignmentKind.Automatic,
                    ActorId = "seeder",
                    OccurredAt = createdAt + TimeSpan.FromMinutes(3),
                });
            }

            var commentCount = chainLength == 0 ? 0 : 1 + (ageSeed % 3);
            for (var comment = 0; comment < commentCount; comment++)
            {
                var occurredAt = createdAt + ((lastActivityAt - createdAt) * (comment + 0.5) / commentCount);
                var fromAgent = comment % 2 == 0;
                var author = fromAgent ? assignedTechnician ?? Technicians[0] : requester;
                dbContext.TicketComments.Add(new TicketComment
                {
                    Id = DeterministicId(CommentKind, index, comment + 1),
                    TicketId = ticket.Id,
                    Body = fromAgent
                        ? AgentUpdates[(ageSeed + comment) % AgentUpdates.Length]
                        : RequesterFollowUps[(ageSeed + comment) % RequesterFollowUps.Length],
                    IsInternal = false,
                    AuthorId = author.Id,
                    AuthorDisplayName = author.DisplayName,
                    CreatedAt = occurredAt,
                });
                counts.Comments++;
            }

            if (chainLength >= 2 && ageSeed % 3 == 0)
            {
                dbContext.TicketComments.Add(new TicketComment
                {
                    Id = DeterministicId(CommentKind, index, 90),
                    TicketId = ticket.Id,
                    Body = InternalNotes[ageSeed % InternalNotes.Length],
                    IsInternal = true,
                    AuthorId = (assignedTechnician ?? Technicians[0]).Id,
                    AuthorDisplayName = (assignedTechnician ?? Technicians[0]).DisplayName,
                    CreatedAt = createdAt + ((lastActivityAt - createdAt) * 0.6),
                });
                counts.Comments++;
            }

            var worklogCount = chainLength >= 2 ? 1 + (ageSeed % 3) : 0;
            for (var worklog = 0; worklog < worklogCount; worklog++)
            {
                dbContext.TicketWorklogs.Add(new TicketWorklog
                {
                    Id = DeterministicId(WorklogKind, index, worklog + 1),
                    TicketId = ticket.Id,
                    Minutes = 15 + (((ageSeed + worklog) % 8) * 15),
                    Note = WorklogNotes[(ageSeed + worklog) % WorklogNotes.Length],
                    AuthorId = (assignedTechnician ?? Technicians[0]).Id,
                    CreatedAt = createdAt + ((lastActivityAt - createdAt) * (worklog + 1) / (worklogCount + 1d)),
                });
                counts.Worklogs++;
            }

            var targets = policyTargets[priority];
            var state = SlaStateFor(isOpen, statusId, index);
            dbContext.TicketSlas.Add(BuildSla(
                DeterministicId(SlaKind, index), ticket.Id, policyIds[priority], targets, state,
                isOpen, statusId, createdAt, lastActivityAt, now));
            counts.Slas++;

            if (counts.Tickets % 25 == 0)
            {
                await dbContext.SaveChangesAsync(cancellationToken);
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(
            calendarsAdded, policiesAdded, counts.Tickets, counts.Comments,
            counts.Worklogs, counts.Transitions, counts.Slas);
    }

    /// <summary>
    /// Picks the SLA state a ticket demonstrates. Open tickets cycle through met, warned, response-breached
    /// and resolution-breached so every state is visible on the list; closed history is mostly met.
    /// </summary>
    private static SlaState SlaStateFor(bool isOpen, Guid statusId, int index)
    {
        if (!isOpen) return index % 7 == 0 ? SlaState.ResolutionBreached : SlaState.Met;
        if (statusId == DefaultTicketStatuses.NewId) return index % 4 == 0 ? SlaState.ResponseWarning : SlaState.Met;
        return (index % 5) switch
        {
            2 => SlaState.ResponseWarning,
            3 => SlaState.ResponseBreached,
            4 => SlaState.ResolutionBreached,
            _ => SlaState.Met,
        };
    }

    /// <summary>
    /// Writes the SLA row with elapsed business seconds and breach flags already consistent with each other,
    /// so <see cref="Features.Sla.SlaEvaluationJob"/> does not re-raise warnings or re-escalate seeded history.
    /// </summary>
    private static TicketSla BuildSla(
        Guid id,
        Guid ticketId,
        Guid policyId,
        (int Response, int Resolution) targets,
        SlaState state,
        bool isOpen,
        Guid statusId,
        DateTimeOffset createdAt,
        DateTimeOffset lastActivityAt,
        DateTimeOffset now)
    {
        var responseTarget = targets.Response * 60d;
        var resolutionTarget = targets.Resolution * 60d;
        var elapsed = state switch
        {
            SlaState.ResponseWarning => responseTarget * 0.9,
            SlaState.ResponseBreached => responseTarget * 1.4,
            SlaState.ResolutionBreached => resolutionTarget * 1.2,
            _ => responseTarget * 0.4,
        };
        var responded = state is not (SlaState.ResponseWarning or SlaState.ResponseBreached);

        // The clock runs for open tickets, is paused while Pending (WP-1.5), and stops once resolved.
        var running = isOpen && statusId != DefaultTicketStatuses.PendingId;
        return new TicketSla
        {
            Id = id,
            TicketId = ticketId,
            PolicyId = policyId,
            StartedAt = createdAt,
            ActiveSince = running ? now : null,
            AccumulatedBusinessSeconds = elapsed,
            ResponseBusinessSeconds = responded ? responseTarget * 0.35 : null,
            ResponseCompletedAt = responded ? createdAt + TimeSpan.FromMinutes(targets.Response * 0.35) : null,
            ResolutionCompletedAt = isOpen ? null : lastActivityAt,
            ResponseWarningRaised = elapsed >= responseTarget * 0.8,
            ResolutionWarningRaised = elapsed >= resolutionTarget * 0.8,
            ResponseBreached = state == SlaState.ResponseBreached,
            ResolutionBreached = state == SlaState.ResolutionBreached,
        };
    }

    private const string LaptopCategoryId = "01980000-0000-7000-8000-000000000511";
    private const int PolicyKind = 1;
    private const int TicketKind = 2;
    private const int CommentKind = 3;
    private const int WorklogKind = 4;
    private const int TransitionKind = 5;
    private const int AssignmentHistoryKind = 6;
    private const int SlaKind = 7;
    private const int CustomFieldValueKind = 8;

    private static Guid DeterministicId(int kind, int index, int child = 0) =>
        Guid.Parse($"01980001-{kind:0000}-7000-8000-{index:0000}{child:00000000}");

    private enum SlaState
    {
        Met,
        ResponseWarning,
        ResponseBreached,
        ResolutionBreached,
    }

    private sealed record TicketTemplate(string CategoryId, TicketType Type, string Title, string Description);

    private sealed class SeedCounters
    {
        public int Tickets { get; set; }
        public int Comments { get; set; }
        public int Worklogs { get; set; }
        public int Transitions { get; set; }
        public int Slas { get; set; }
    }
}
