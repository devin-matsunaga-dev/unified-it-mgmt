using System.Security.Claims;
using System.Net.Mail;

using Contracts.Events;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Modules.Helpdesk.Data;
using Platform.Auditing;
using Platform.Directory;
using Platform.Notifications;
using Platform.Search;
using Modules.Helpdesk.Features.Sla;
using Modules.Helpdesk.Features.Categories;

namespace Modules.Helpdesk.Features.Tickets;

public sealed class TicketService(
    HelpdeskDbContext dbContext,
    IPublishEndpoint publishEndpoint,
    IAuditService auditService,
    ISlaService slaService,
    INotificationService notificationService,
    IDirectoryService directoryService) : ITicketService
{
    /// <summary>
    /// The text-search dictionary the generated tsvector columns are built with. One definition for the
    /// whole solution since WP-5.4 — a column generated with one dictionary and queried with another
    /// silently stops matching anything that stems.
    /// </summary>
    internal const string SearchConfiguration = SearchTerm.Configuration;

    public async Task<TicketWriteResult> CreateAsync(
        CreateTicketRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var category = await ResolveCategoryAsync(request.CategoryId, cancellationToken);
        if (request.CategoryId is not null && category is null)
        {
            return new(TicketWriteOutcome.CategoryNotFound);
        }

        var bound = CustomFieldValueBinder.Bind([.. category?.Fields ?? []], request.CustomFields);
        if (bound.Errors.Count > 0)
        {
            return new(TicketWriteOutcome.InvalidCustomFields, Errors: bound.Errors);
        }

        var requesterId = IsEndUser(actor) ? GetActorId(actor) : request.RequesterId ?? GetActorId(actor);
        var requesterName = request.RequesterId is null || IsEndUser(actor)
            ? GetActorDisplayName(actor)
            : request.RequesterId;
        var requesterEmail = ValidEmailOrNull(request.RequesterId)
            ?? (request.RequesterId is null || IsEndUser(actor) ? GetActorEmail(actor) : null);
        TicketQueue? queue = null;
        string? assignedTechnicianId = null;
        if (request.QueueId is not null)
        {
            queue = await dbContext.TicketQueues.Include(item => item.Team).ThenInclude(team => team.Members)
                .SingleOrDefaultAsync(item => item.Id == request.QueueId, cancellationToken);
            if (queue is null)
            {
                return new(TicketWriteOutcome.QueueNotFound);
            }

            var technicians = queue.Team.Members.Select(member => member.TechnicianId)
                .OrderBy(id => id, StringComparer.Ordinal).ToList();
            if (technicians.Count > 0)
            {
                var lastIndex = queue.LastAssignedTechnicianId is null
                    ? -1
                    : technicians.FindIndex(id => id == queue.LastAssignedTechnicianId);
                assignedTechnicianId = technicians[(lastIndex + 1) % technicians.Count];
                queue.LastAssignedTechnicianId = assignedTechnicianId;
            }
        }

        var ticket = new Ticket
        {
            Id = Guid.CreateVersion7(),
            Title = request.Title.Trim(),
            Description = request.Description.Trim(),
            Type = request.Type,
            Urgency = request.Urgency,
            Impact = request.Impact,
            Priority = TicketPriorityMatrix.Calculate(request.Urgency, request.Impact),
            StatusId = DefaultTicketStatuses.NewId,
            RequesterId = requesterId,
            RequesterDisplayName = requesterName,
            RequesterEmail = requesterEmail,
            QueueId = queue?.Id,
            Queue = queue,
            AssignedTechnicianId = assignedTechnicianId,
            CategoryId = category?.Id,
            Category = category,
            CreatedAt = now,
            UpdatedAt = now,
        };
        foreach (var (fieldId, value) in bound.Values)
        {
            ticket.CustomFieldValues.Add(new TicketCustomFieldValue
            {
                Id = Guid.CreateVersion7(), TicketId = ticket.Id, FieldId = fieldId,
                Field = category!.Fields.Single(field => field.Id == fieldId), Value = value, UpdatedAt = now,
            });
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        dbContext.Tickets.Add(ticket);
        if (queue is not null && assignedTechnicianId is not null)
        {
            dbContext.TicketAssignmentHistory.Add(new TicketAssignmentHistory
            {
                Id = Guid.CreateVersion7(), TicketId = ticket.Id, QueueId = queue.Id,
                ToTechnicianId = assignedTechnicianId, Kind = AssignmentKind.Automatic,
                ActorId = GetActorId(actor), OccurredAt = now,
            });
        }
        await dbContext.SaveChangesAsync(cancellationToken);
        await dbContext.Entry(ticket).Reference(item => item.Status).LoadAsync(cancellationToken);
        await slaService.StartAsync(ticket, now, cancellationToken);
        await publishEndpoint.Publish(new TicketCreated(
            Guid.CreateVersion7(), now, ticket.Id, ticket.Number, ticket.RequesterId,
            ticket.Type.ToString(), ticket.Priority.ToString()), cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var response = Map(ticket);
        await auditService.WriteAsync(actor, "Created", "Ticket", ticket.Id.ToString(), null, response, cancellationToken);
        await NotifyAsync(ticket, "TicketCreated", "created", cancellationToken);
        return new(TicketWriteOutcome.Success, response);
    }

    public async Task<TicketResponse?> GetAsync(
        Guid id,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        var ticket = await VisibleTickets(actor).SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (ticket is null) return null;
        return (await WithRequesterPlaceAsync([Map(ticket)], cancellationToken))[0];
    }

    public async Task<TicketListResult> ListAsync(
        TicketListFilter filter,
        int page,
        int pageSize,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        var query = VisibleTickets(actor);

        if (filter.Statuses is { Count: > 0 })
        {
            var requested = filter.Statuses.Where(status => !string.IsNullOrWhiteSpace(status))
                .Select(status => status.Trim()).ToList();
            var known = await dbContext.TicketStatuses.Select(status => status.Name).ToListAsync(cancellationToken);
            var unknown = requested
                .Where(status => !known.Contains(status, StringComparer.OrdinalIgnoreCase)).ToList();
            if (unknown.Count > 0)
            {
                return new(null, new Dictionary<string, string[]>
                {
                    ["status"] = [.. unknown.Select(status => $"Status '{status}' does not exist.")],
                });
            }

            var names = known.Where(name => requested.Contains(name, StringComparer.OrdinalIgnoreCase)).ToList();
            query = query.Where(ticket => names.Contains(ticket.Status.Name));
        }

        if (filter.Priorities is { Count: > 0 })
        {
            var priorities = filter.Priorities.ToList();
            query = query.Where(ticket => priorities.Contains(ticket.Priority));
        }

        if (filter.Type is { } type)
        {
            query = query.Where(ticket => ticket.Type == type);
        }

        if (filter.QueueId is { } queueId)
        {
            query = query.Where(ticket => ticket.QueueId == queueId);
        }

        if (filter.CategoryId is { } categoryId)
        {
            query = query.Where(ticket => ticket.CategoryId == categoryId);
        }

        if (filter.CiId is { } ciId)
        {
            query = query.Where(ticket => dbContext.TicketCiLinks.Any(
                link => link.TicketId == ticket.Id && link.CiId == ciId));
        }

        if (!string.IsNullOrWhiteSpace(filter.RequesterId))
        {
            var requesterId = filter.RequesterId.Trim();
            query = query.Where(ticket => ticket.RequesterId == requesterId);
        }

        if (filter.Unassigned)
        {
            query = query.Where(ticket => ticket.AssignedTechnicianId == null);
        }
        else if (!string.IsNullOrWhiteSpace(filter.AssignedTechnicianId))
        {
            var technicianId = filter.AssignedTechnicianId.Trim();
            query = query.Where(ticket => ticket.AssignedTechnicianId == technicianId);
        }

        var term = TicketSearchQuery.ToPrefixTsQuery(filter.Search);
        var sequenceNumber = TicketSearchQuery.ToSequenceNumber(filter.Search);
        if (term is not null)
        {
            // Internal notes are searchable for agents only; a requester must never learn a note exists.
            var includeInternal = !IsEndUser(actor);
            query = query.Where(ticket =>
                ticket.SearchVector.Matches(EF.Functions.ToTsQuery(SearchConfiguration, term))
                || (sequenceNumber != null && ticket.SequenceNumber == sequenceNumber)
                || dbContext.TicketComments.Any(comment =>
                    comment.TicketId == ticket.Id
                    && (includeInternal || !comment.IsInternal)
                    && comment.SearchVector.Matches(EF.Functions.ToTsQuery(SearchConfiguration, term))));
        }

        var total = await query.CountAsync(cancellationToken);
        var ordered = term is null
            ? query.OrderByDescending(ticket => ticket.CreatedAt)
            : query
                .OrderByDescending(ticket =>
                    ticket.SearchVector.Rank(EF.Functions.ToTsQuery(SearchConfiguration, term)))
                .ThenByDescending(ticket => ticket.CreatedAt);
        var tickets = await ordered
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
        var mapped = await WithRequesterPlaceAsync([.. tickets.Select(Map)], cancellationToken);
        return new(new TicketPageResponse(mapped, total, page, pageSize));
    }

    public async Task<TicketWriteResult> UpdateAsync(
        Guid id,
        UpdateTicketRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        var ticket = await VisibleTickets(actor).SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (ticket is null)
        {
            return new(TicketWriteOutcome.TicketNotFound);
        }

        var category = await ResolveCategoryAsync(request.CategoryId, cancellationToken);
        if (request.CategoryId is not null && category is null)
        {
            return new(TicketWriteOutcome.CategoryNotFound);
        }

        var bound = CustomFieldValueBinder.Bind([.. category?.Fields ?? []], request.CustomFields);
        if (bound.Errors.Count > 0)
        {
            return new(TicketWriteOutcome.InvalidCustomFields, Errors: bound.Errors);
        }

        var before = Map(ticket);
        ticket.Title = request.Title.Trim();
        ticket.Description = request.Description.Trim();
        ticket.Type = request.Type;
        ticket.Urgency = request.Urgency;
        ticket.Impact = request.Impact;
        ticket.Priority = TicketPriorityMatrix.Calculate(request.Urgency, request.Impact);
        ticket.CategoryId = category?.Id;
        ticket.Category = category;
        ticket.UpdatedAt = DateTimeOffset.UtcNow;
        ApplyCustomFieldValues(ticket, category, bound.Values);

        await publishEndpoint.Publish(new TicketUpdated(
            Guid.CreateVersion7(), ticket.UpdatedAt, ticket.Id, ticket.Number, ticket.RequesterId,
            ticket.Type.ToString(), ticket.Priority.ToString()), cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);

        var after = Map(ticket);
        await auditService.WriteAsync(actor, "Updated", "Ticket", ticket.Id.ToString(), before, after, cancellationToken);
        await NotifyAsync(ticket, "TicketUpdated", "updated", cancellationToken);
        return new(TicketWriteOutcome.Success, after);
    }

    private async Task<TicketCategory?> ResolveCategoryAsync(Guid? categoryId, CancellationToken cancellationToken) =>
        categoryId is null
            ? null
            : await dbContext.TicketCategories.Include(category => category.Fields)
                .SingleOrDefaultAsync(category => category.Id == categoryId && category.IsActive, cancellationToken);

    private void ApplyCustomFieldValues(
        Ticket ticket,
        TicketCategory? category,
        IReadOnlyDictionary<Guid, string> values)
    {
        foreach (var existing in ticket.CustomFieldValues.ToList())
        {
            if (values.TryGetValue(existing.FieldId, out var value))
            {
                existing.Value = value;
                existing.UpdatedAt = ticket.UpdatedAt;
            }
            else
            {
                dbContext.TicketCustomFieldValues.Remove(existing);
                ticket.CustomFieldValues.Remove(existing);
            }
        }

        foreach (var (fieldId, value) in values.Where(
                     entry => ticket.CustomFieldValues.All(item => item.FieldId != entry.Key)))
        {
            ticket.CustomFieldValues.Add(new TicketCustomFieldValue
            {
                Id = Guid.CreateVersion7(), TicketId = ticket.Id, FieldId = fieldId,
                Field = category!.Fields.Single(field => field.Id == fieldId), Value = value,
                UpdatedAt = ticket.UpdatedAt,
            });
        }
    }

    public async Task<TransitionTicketResult> TransitionAsync(
        Guid id,
        TransitionTicketRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        var ticket = await VisibleTickets(actor).SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (ticket is null)
        {
            return new(TransitionTicketOutcome.NotFound);
        }

        var targetStatus = await dbContext.TicketStatuses.SingleOrDefaultAsync(
            status => status.Name.ToLower() == request.TargetStatus.Trim().ToLower(), cancellationToken);
        if (targetStatus is null)
        {
            return new(TransitionTicketOutcome.UnknownStatus, Error: $"Status '{request.TargetStatus}' does not exist.");
        }

        if (IsEndUser(actor) && !(ticket.StatusId == DefaultTicketStatuses.ResolvedId
                && targetStatus.Id == DefaultTicketStatuses.ClosedId))
        {
            return new(
                TransitionTicketOutcome.Forbidden,
                Error: "Requesters may only close a resolved ticket.");
        }

        var allowed = await dbContext.TicketStatusTransitions.AnyAsync(
            transition => transition.FromStatusId == ticket.StatusId && transition.ToStatusId == targetStatus.Id,
            cancellationToken);
        if (!allowed)
        {
            return new(
                TransitionTicketOutcome.IllegalTransition,
                Error: $"Transition from '{ticket.Status.Name}' to '{targetStatus.Name}' is not allowed.");
        }

        var resolutionNote = string.IsNullOrWhiteSpace(request.ResolutionNote) ? null : request.ResolutionNote.Trim();
        if (targetStatus.RequiresResolutionNote && resolutionNote is null)
        {
            return new(
                TransitionTicketOutcome.ResolutionNoteRequired,
                Error: $"A resolution note is required when transitioning to '{targetStatus.Name}'.");
        }

        var actorId = GetActorId(actor);
        var occurredAt = DateTimeOffset.UtcNow;
        var before = Map(ticket);
        var fromStatusId = ticket.StatusId;
        var history = new TicketTransitionHistory
        {
            Id = Guid.CreateVersion7(),
            TicketId = ticket.Id,
            FromStatusId = ticket.StatusId,
            ToStatusId = targetStatus.Id,
            ResolutionNote = resolutionNote,
            ActorId = actorId,
            OccurredAt = occurredAt,
        };

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        dbContext.TicketTransitionHistory.Add(history);
        ticket.StatusId = targetStatus.Id;
        ticket.Status = targetStatus;
        ticket.UpdatedAt = occurredAt;
        await slaService.RecordStatusChangeAsync(ticket, fromStatusId, occurredAt, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await publishEndpoint.Publish(new TicketStatusChanged(
            Guid.CreateVersion7(), occurredAt, ticket.Id, ticket.Number,
            before.Status, targetStatus.Name, actorId), cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var after = Map(ticket);
        await auditService.WriteAsync(
            actor, "StatusChanged", "Ticket", ticket.Id.ToString(), before, after, cancellationToken);
        if (targetStatus.Name.Equals("Resolved", StringComparison.OrdinalIgnoreCase))
        {
            await NotifyAsync(ticket, "TicketResolved", "resolved", cancellationToken);
        }
        return new(TransitionTicketOutcome.Success, after);
    }

    public async Task<IReadOnlyList<TicketTransitionResponse>?> GetTransitionHistoryAsync(
        Guid id,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        if (!await VisibleTickets(actor).AnyAsync(ticket => ticket.Id == id, cancellationToken))
        {
            return null;
        }

        return await dbContext.TicketTransitionHistory
            .Where(history => history.TicketId == id)
            .OrderBy(history => history.OccurredAt)
            .ThenBy(history => history.Id)
            .Select(history => new TicketTransitionResponse(
                history.Id,
                history.TicketId,
                history.FromStatus.Name,
                history.ToStatus.Name,
                history.ResolutionNote,
                history.ActorId,
                history.OccurredAt))
            .ToListAsync(cancellationToken);
    }

    private IQueryable<Ticket> VisibleTickets(ClaimsPrincipal actor)
    {
        var query = dbContext.Tickets.Include(ticket => ticket.Status).Include(ticket => ticket.Queue)
            .Include(ticket => ticket.Category)
            .Include(ticket => ticket.CustomFieldValues).ThenInclude(value => value.Field)
            .AsQueryable();
        return IsEndUser(actor) ? query.Where(ticket => ticket.RequesterId == GetActorId(actor)) : query;
    }

    /// <summary>
    /// Fills in each requester's department and location from Platform's directory.
    /// <para>
    /// The directory is read <b>once</b> for the whole page rather than per ticket, because a 25-row
    /// list would otherwise be 25 round trips for two strings each.
    /// </para>
    /// <para>
    /// The join is on username first and email second, which is the convention the rest of the app
    /// already follows — <c>Ticket.RequesterId</c> holds the identity the helpdesk recorded, which is
    /// the username for seeded and agent-raised tickets and the OIDC subject for a portal one. It is
    /// deliberately not <c>UserProfile.Id</c>: Keycloak mints its own user ids, so the subject claim
    /// matches no row in the directory. A requester who resolves to nobody simply has no place shown.
    /// </para>
    /// </summary>
    private async Task<IReadOnlyList<TicketResponse>> WithRequesterPlaceAsync(
        IReadOnlyList<TicketResponse> tickets,
        CancellationToken cancellationToken)
    {
        if (tickets.Count == 0)
        {
            return tickets;
        }

        var users = await directoryService.ListUsersAsync(cancellationToken);
        var byUsername = users
            .GroupBy(user => user.Username, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var byEmail = users
            .Where(user => !string.IsNullOrWhiteSpace(user.Email))
            .GroupBy(user => user.Email, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        return [.. tickets.Select(ticket =>
        {
            if (!byUsername.TryGetValue(ticket.RequesterId, out var user)
                && !byEmail.TryGetValue(ticket.RequesterId, out user))
            {
                return ticket;
            }

            return ticket with
            {
                RequesterDepartmentName = user.DepartmentName,
                RequesterSiteName = user.SiteName,
            };
        })];
    }

    private static bool IsEndUser(ClaimsPrincipal actor) => actor.IsInRole("EndUser");

    private static string GetActorId(ClaimsPrincipal actor) =>
        actor.FindFirstValue("sub") ?? actor.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException("An authenticated actor identifier is required.");

    private static string GetActorDisplayName(ClaimsPrincipal actor) =>
        actor.FindFirstValue("name") ?? actor.Identity?.Name ?? actor.FindFirstValue("preferred_username")
        ?? GetActorId(actor);

    private static string? GetActorEmail(ClaimsPrincipal actor) =>
        ValidEmailOrNull(actor.FindFirstValue(ClaimTypes.Email) ?? actor.FindFirstValue("email"));

    private static string? ValidEmailOrNull(string? value) =>
        MailAddress.TryCreate(value, out var address) ? address.Address : null;

    private Task NotifyAsync(Ticket ticket, string templateName, string action, CancellationToken cancellationToken) =>
        notificationService.SendAsync(new NotificationMessage(
            ticket.RequesterEmail ?? string.Empty,
            new NotificationTemplate(
                templateName,
                $"[{ticket.Number}] Ticket {action}: {ticket.Title}",
                $"Your ticket {ticket.Number} has been {action}. Reply to this email to add a comment."),
            new { TicketId = ticket.Id, TicketNumber = ticket.Number, ticket.Title },
            new Dictionary<string, string>
            {
                ["Message-Id"] = $"<ticket-{ticket.Id:N}@it-platform.local>",
                ["X-IT-Platform-Ticket-Id"] = ticket.Id.ToString(),
            }), cancellationToken);

    internal static TicketResponse Map(Ticket ticket) => new(
        ticket.Id,
        ticket.Number,
        ticket.Title,
        ticket.Description,
        ticket.Type,
        ticket.Urgency,
        ticket.Impact,
        ticket.Priority,
        ticket.Status.Name,
        ticket.RequesterId,
        ticket.RequesterDisplayName ?? ticket.RequesterId,
        ticket.QueueId,
        ticket.Queue?.Name,
        ticket.AssignedTechnicianId,
        ticket.CreatedAt,
        ticket.UpdatedAt,
        ticket.CategoryId,
        ticket.Category?.Name,
        [.. ticket.CustomFieldValues
            .OrderBy(value => value.Field.SortOrder).ThenBy(value => value.Field.Label)
            .Select(value => new TicketCustomFieldValueResponse(
                value.FieldId, value.Field.Key, value.Field.Label, value.Field.Type, value.Value))]);
}
