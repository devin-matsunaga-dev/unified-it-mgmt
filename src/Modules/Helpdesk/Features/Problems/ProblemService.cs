using System.Security.Claims;

using Microsoft.EntityFrameworkCore;

using Modules.Helpdesk.Data;
using Platform.Actors;
using Platform.Auditing;
using Platform.Integration;

using Modules.Helpdesk.Features.Tickets;

namespace Modules.Helpdesk.Features.Problems;

public sealed class ProblemService(
    HelpdeskDbContext dbContext,
    IAuditService auditService,
    ICiDirectory ciDirectory) : IProblemService
{
    private const int MaximumPageSize = 200;

    /// <summary>Incidents rendered beside one problem. Beyond this the panel is a list nobody scrolls.</summary>
    private const int MaximumIncidentsShown = 200;

    public async Task<ProblemPageResponse> ListAsync(
        ProblemListFilter filter,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (!ActorRoles.IsAgent(actor))
        {
            return new ProblemPageResponse([], 0, 1, filter.PageSize);
        }

        var page = Math.Max(filter.Page, 1);
        var pageSize = Math.Clamp(filter.PageSize, 1, MaximumPageSize);

        var query = dbContext.Problems.AsNoTracking().AsQueryable();

        if (filter.Statuses is { Count: > 0 } statuses)
        {
            // Contains rather than a range: every enum here is stored as text and a comparison would be
            // a comparison of words (WP-5.6).
            var wanted = statuses.Distinct().ToArray();
            query = query.Where(problem => wanted.Contains(problem.Status));
        }

        if (filter.KnownErrorsOnly)
        {
            query = query.Where(problem => problem.Status == ProblemStatus.KnownError);
        }

        if (filter.CiId is { } ciId)
        {
            query = query.Where(problem => problem.CiId == ciId);
        }

        if (filter.CategoryId is { } categoryId)
        {
            query = query.Where(problem => problem.CategoryId == categoryId);
        }

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            // The known-error database is only worth having if somebody can find a row in it while
            // holding an incident, so the workaround and the cause are searched as well as the title.
            var term = $"%{filter.Search.Trim()}%";
            query = query.Where(problem =>
                EF.Functions.ILike(problem.Title, term)
                || EF.Functions.ILike(problem.Description, term)
                || (problem.RootCause != null && EF.Functions.ILike(problem.RootCause, term))
                || (problem.Workaround != null && EF.Functions.ILike(problem.Workaround, term)));
        }

        var total = await query.CountAsync(cancellationToken);
        var problems = await query
            .OrderByDescending(problem => problem.CreatedAt)
            .ThenBy(problem => problem.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        var counts = await IncidentCountsAsync([.. problems.Select(problem => problem.Id)], cancellationToken);
        var subjects = await SubjectsAsync(problems, cancellationToken);
        return new ProblemPageResponse(
            [.. problems.Select(problem => Map(problem, subjects, counts.GetValueOrDefault(problem.Id)))],
            total,
            page,
            pageSize);
    }

    public async Task<ProblemResponse?> GetAsync(Guid id, ClaimsPrincipal actor, CancellationToken cancellationToken)
    {
        if (!ActorRoles.IsAgent(actor))
        {
            return null;
        }

        var problem = await dbContext.Problems.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (problem is null)
        {
            return null;
        }

        var incidents = await IncidentsAsync(id, cancellationToken);
        var subjects = await SubjectsAsync([problem], cancellationToken);
        return Map(problem, subjects, incidents.Count, incidents);
    }

    public async Task<ProblemResult> CreateAsync(
        CreateProblemRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!ActorRoles.IsAgent(actor))
        {
            return new(ProblemOutcome.Forbidden, Error: "Problems are an agent surface.");
        }

        if (await SubjectErrorsAsync(request.CiId, request.CategoryId, cancellationToken) is { } subjectErrors)
        {
            return new(ProblemOutcome.Invalid, Errors: subjectErrors);
        }

        var now = DateTimeOffset.UtcNow;
        var problem = new Problem
        {
            Id = Guid.CreateVersion7(),
            Title = request.Title.Trim(),
            Description = request.Description.Trim(),
            Status = ProblemStatus.Investigating,
            Priority = request.Priority,
            CiId = request.CiId,
            CategoryId = request.CategoryId,
            RootCause = Trimmed(request.RootCause),
            Workaround = Trimmed(request.Workaround),
            AssignedTechnicianId = Trimmed(request.AssignedTechnicianId),
            OpenedById = ActorId(actor),
            OpenedByName = ActorDisplayName(actor),
            CreatedAt = now,
            UpdatedAt = now,
        };
        dbContext.Problems.Add(problem);

        if (request.IncidentIds is { Count: > 0 } incidentIds)
        {
            var attach = await AttachableIncidentsAsync(incidentIds, cancellationToken);
            foreach (var ticketId in attach)
            {
                dbContext.ProblemIncidents.Add(NewLink(problem.Id, ticketId, actor, now));
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        var response = await ReadBackAsync(problem.Id, cancellationToken);
        await auditService.WriteAsync(
            actor, "Created", "Problem", problem.Id.ToString(), null, response, cancellationToken);
        return new(ProblemOutcome.Success, response);
    }

    public async Task<ProblemResult> UpdateAsync(
        Guid id,
        UpdateProblemRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!ActorRoles.IsAgent(actor))
        {
            return new(ProblemOutcome.Forbidden, Error: "Problems are an agent surface.");
        }

        var problem = await dbContext.Problems.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (problem is null)
        {
            return new(ProblemOutcome.NotFound);
        }

        if (await SubjectErrorsAsync(request.CiId, request.CategoryId, cancellationToken) is { } subjectErrors)
        {
            return new(ProblemOutcome.Invalid, Errors: subjectErrors);
        }

        var before = await ReadBackAsync(id, cancellationToken);
        problem.Title = request.Title.Trim();
        problem.Description = request.Description.Trim();
        problem.Priority = request.Priority;
        problem.CiId = request.CiId;
        problem.CategoryId = request.CategoryId;
        problem.RootCause = Trimmed(request.RootCause);
        problem.Workaround = Trimmed(request.Workaround);
        problem.AssignedTechnicianId = Trimmed(request.AssignedTechnicianId);
        problem.UpdatedAt = DateTimeOffset.UtcNow;

        // A known error whose cause or workaround has been erased is no longer a known error, and leaving
        // it in that state would put a row in the database that answers nothing. Putting it back to
        // investigation is the honest reading of the edit, and the audit entry records that it happened.
        if (problem.Status == ProblemStatus.KnownError
            && (problem.RootCause is null || problem.Workaround is null))
        {
            problem.Status = ProblemStatus.Investigating;
            problem.KnownErrorAt = null;
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        var after = await ReadBackAsync(id, cancellationToken);
        await auditService.WriteAsync(actor, "Updated", "Problem", id.ToString(), before, after, cancellationToken);
        return new(ProblemOutcome.Success, after);
    }

    public async Task<ProblemTransitionResult> TransitionAsync(
        Guid id,
        ProblemTransitionRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!ActorRoles.IsAgent(actor))
        {
            return new(ProblemOutcome.Forbidden, Error: "Problems are an agent surface.");
        }

        var problem = await dbContext.Problems.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (problem is null)
        {
            return new(ProblemOutcome.NotFound);
        }

        var resolution = Trimmed(request.Resolution) ?? problem.Resolution;
        var verdict = ProblemWorkflow.Check(problem, request.TargetStatus, resolution);
        if (verdict != ProblemTransitionVerdict.Allowed)
        {
            var explanation = ProblemWorkflow.Explain(problem.Status, request.TargetStatus, verdict);
            return verdict switch
            {
                // A missing cause, workaround or resolution is a fact about the request, so it comes back
                // as a field error the form can point at. A move the workflow does not make is a fact
                // about the problem's state and comes back as a conflict.
                ProblemTransitionVerdict.NeedsCauseAndWorkaround => new(
                    ProblemOutcome.Invalid,
                    Errors: new Dictionary<string, string[]>
                    {
                        [nameof(UpdateProblemRequest.Workaround)] = [explanation],
                    }),
                ProblemTransitionVerdict.NeedsResolution => new(
                    ProblemOutcome.Invalid,
                    Errors: new Dictionary<string, string[]>
                    {
                        [nameof(ProblemTransitionRequest.Resolution)] = [explanation],
                    }),
                _ => new(ProblemOutcome.InvalidTransition, Error: explanation),
            };
        }

        var before = await ReadBackAsync(id, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        problem.Status = request.TargetStatus;
        problem.Resolution = resolution;
        problem.UpdatedAt = now;
        switch (request.TargetStatus)
        {
            case ProblemStatus.KnownError:
                problem.KnownErrorAt ??= now;
                break;
            case ProblemStatus.Resolved:
                problem.ResolvedAt = now;
                problem.ClosedAt = null;
                break;
            case ProblemStatus.Closed:
                problem.ResolvedAt ??= now;
                problem.ClosedAt = now;
                break;
            case ProblemStatus.Investigating:
                // Reopening clears the endings but keeps the resolution text, which is now the record of
                // what was tried and did not hold.
                problem.ResolvedAt = null;
                problem.ClosedAt = null;
                problem.KnownErrorAt = null;
                break;
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        var after = await ReadBackAsync(id, cancellationToken);
        await auditService.WriteAsync(
            actor, $"TransitionedTo{request.TargetStatus}", "Problem", id.ToString(), before, after, cancellationToken);

        // The prompt the WP asks for, and only on the act it names. Composed here rather than fetched by
        // the browser afterwards so that a failed second request cannot lose it.
        var draft = request.TargetStatus == ProblemStatus.Closed
            ? await ComposeDraftAsync(problem, cancellationToken)
            : null;
        return new(ProblemOutcome.Success, after, draft);
    }

    public async Task<ProblemResult> LinkIncidentAsync(
        Guid id,
        Guid ticketId,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        if (!ActorRoles.IsAgent(actor))
        {
            return new(ProblemOutcome.Forbidden, Error: "Problems are an agent surface.");
        }

        if (!await dbContext.Problems.AnyAsync(problem => problem.Id == id, cancellationToken))
        {
            return new(ProblemOutcome.NotFound);
        }

        var ticket = await dbContext.Tickets.AsNoTracking()
            .Where(item => item.Id == ticketId)
            .Select(item => new { item.Id, item.Type })
            .SingleOrDefaultAsync(cancellationToken);
        if (ticket is null)
        {
            return new(ProblemOutcome.Invalid, Errors: Field(
                nameof(ticketId), "That ticket does not exist."));
        }

        if (ticket.Type != TicketType.Incident)
        {
            return new(ProblemOutcome.Invalid, Errors: Field(
                nameof(ticketId),
                "Only incidents can be linked to a problem — a service request is somebody asking for "
                + "something, not a symptom of a fault."));
        }

        var existing = await dbContext.ProblemIncidents.AsNoTracking()
            .SingleOrDefaultAsync(link => link.TicketId == ticketId, cancellationToken);
        if (existing is not null)
        {
            return new(
                ProblemOutcome.Duplicate,
                Error: existing.ProblemId == id
                    ? "That incident is already linked to this problem."
                    : "That incident already belongs to another problem. Unlink it there first.");
        }

        dbContext.ProblemIncidents.Add(NewLink(id, ticketId, actor, DateTimeOffset.UtcNow));
        await dbContext.SaveChangesAsync(cancellationToken);

        var after = await ReadBackAsync(id, cancellationToken);
        await auditService.WriteAsync(
            actor, "IncidentLinked", "Problem", id.ToString(), null, new { TicketId = ticketId }, cancellationToken);
        return new(ProblemOutcome.Success, after);
    }

    public async Task<ProblemOutcome> UnlinkIncidentAsync(
        Guid id,
        Guid ticketId,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        if (!ActorRoles.IsAgent(actor))
        {
            return ProblemOutcome.Forbidden;
        }

        var link = await dbContext.ProblemIncidents
            .SingleOrDefaultAsync(item => item.ProblemId == id && item.TicketId == ticketId, cancellationToken);
        if (link is null)
        {
            return ProblemOutcome.NotFound;
        }

        dbContext.ProblemIncidents.Remove(link);
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditService.WriteAsync(
            actor, "IncidentUnlinked", "Problem", id.ToString(), new { TicketId = ticketId }, null, cancellationToken);
        return ProblemOutcome.Success;
    }

    public async Task<KnowledgeDraftResponse?> GetKnowledgeDraftAsync(
        Guid id,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        if (!ActorRoles.IsAgent(actor))
        {
            return null;
        }

        var problem = await dbContext.Problems.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        return problem is null ? null : await ComposeDraftAsync(problem, cancellationToken);
    }

    public async Task<IReadOnlyList<ProblemResponse>> ListForTicketAsync(
        Guid ticketId,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        if (!ActorRoles.IsAgent(actor))
        {
            return [];
        }

        var problemIds = await dbContext.ProblemIncidents.AsNoTracking()
            .Where(link => link.TicketId == ticketId)
            .Select(link => link.ProblemId)
            .ToListAsync(cancellationToken);
        if (problemIds.Count == 0)
        {
            return [];
        }

        var problems = await dbContext.Problems.AsNoTracking()
            .Where(problem => problemIds.Contains(problem.Id))
            .OrderByDescending(problem => problem.CreatedAt)
            .ToListAsync(cancellationToken);
        var counts = await IncidentCountsAsync(problemIds, cancellationToken);
        var subjects = await SubjectsAsync(problems, cancellationToken);
        return [.. problems.Select(problem => Map(problem, subjects, counts.GetValueOrDefault(problem.Id)))];
    }

    // ---- shared internals, also used by the suggestion service ----

    internal static ProblemIncident NewLink(Guid problemId, Guid ticketId, ClaimsPrincipal actor, DateTimeOffset now) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            ProblemId = problemId,
            TicketId = ticketId,
            LinkedById = ActorId(actor),
            LinkedByName = ActorDisplayName(actor),
            LinkedAt = now,
        };

    /// <summary>
    /// Of the ids offered, the incidents that exist and are not already spoken for.
    /// <para>
    /// Filtered rather than refused because the caller is usually the accept path, which offers every
    /// incident the pass counted — and one of them having been attached to another problem in the
    /// meantime is a race, not a mistake worth failing the whole write over.
    /// </para>
    /// </summary>
    internal async Task<IReadOnlyList<Guid>> AttachableIncidentsAsync(
        IReadOnlyCollection<Guid> ticketIds,
        CancellationToken cancellationToken)
    {
        if (ticketIds.Count == 0)
        {
            return [];
        }

        var wanted = ticketIds.Distinct().ToArray();
        var incidents = await dbContext.Tickets.AsNoTracking()
            .Where(ticket => wanted.Contains(ticket.Id) && ticket.Type == TicketType.Incident)
            .Select(ticket => ticket.Id)
            .ToListAsync(cancellationToken);
        var taken = await dbContext.ProblemIncidents.AsNoTracking()
            .Where(link => wanted.Contains(link.TicketId))
            .Select(link => link.TicketId)
            .ToListAsync(cancellationToken);
        return [.. incidents.Except(taken)];
    }

    internal async Task<ProblemResponse> ReadBackAsync(Guid id, CancellationToken cancellationToken)
    {
        var problem = await dbContext.Problems.AsNoTracking().SingleAsync(item => item.Id == id, cancellationToken);
        var incidents = await IncidentsAsync(id, cancellationToken);
        var subjects = await SubjectsAsync([problem], cancellationToken);
        return Map(problem, subjects, incidents.Count, incidents);
    }

    internal async Task<IReadOnlyList<ProblemIncidentResponse>> IncidentsAsync(
        Guid problemId,
        CancellationToken cancellationToken)
    {
        // Flat columns rather than Include-then-Select: EF ignores an Include the moment a query projects
        // a shape other than the entity it started from, and the navigation would arrive null (WP-5.5).
        var rows = await dbContext.ProblemIncidents.AsNoTracking()
            .Where(link => link.ProblemId == problemId)
            .OrderByDescending(link => link.Ticket.CreatedAt)
            .Take(MaximumIncidentsShown)
            .Select(link => new
            {
                link.TicketId,
                link.Ticket.SequenceNumber,
                link.Ticket.Type,
                link.Ticket.Title,
                Status = link.Ticket.Status.Name,
                link.Ticket.Priority,
                link.Ticket.CreatedAt,
                link.LinkedById,
                link.LinkedByName,
                link.LinkedAt,
            })
            .ToListAsync(cancellationToken);

        return [.. rows.Select(row => new ProblemIncidentResponse(
            row.TicketId,
            TicketNumber.Format(row.Type, row.SequenceNumber),
            row.Title,
            row.Status,
            row.Priority,
            row.CreatedAt,
            row.LinkedById,
            row.LinkedByName,
            row.LinkedAt))];
    }

    private async Task<KnowledgeDraftResponse> ComposeDraftAsync(Problem problem, CancellationToken cancellationToken)
    {
        var incidents = await IncidentsAsync(problem.Id, cancellationToken);
        var subjects = await SubjectsAsync([problem], cancellationToken);
        return KnowledgeDraft.Compose(problem, Subject(problem, subjects)?.Name, incidents);
    }

    private async Task<Dictionary<Guid, int>> IncidentCountsAsync(
        IReadOnlyCollection<Guid> problemIds,
        CancellationToken cancellationToken)
    {
        if (problemIds.Count == 0)
        {
            return [];
        }

        var ids = problemIds.Distinct().ToArray();
        return await dbContext.ProblemIncidents.AsNoTracking()
            .Where(link => ids.Contains(link.ProblemId))
            .GroupBy(link => link.ProblemId)
            .Select(group => new { ProblemId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.ProblemId, row => row.Count, cancellationToken);
    }

    /// <summary>
    /// Names for every subject these problems point at, in two reads: one to the CMDB through the port,
    /// one to this schema's own categories. Never per problem — a board of twenty-five problems must not
    /// be twenty-five round trips.
    /// </summary>
    internal async Task<SubjectNames> SubjectsAsync(
        IReadOnlyCollection<Problem> problems,
        CancellationToken cancellationToken)
    {
        var ciIds = problems.Where(problem => problem.CiId is not null)
            .Select(problem => problem.CiId!.Value).Distinct().ToArray();
        var categoryIds = problems.Where(problem => problem.CategoryId is not null)
            .Select(problem => problem.CategoryId!.Value).Distinct().ToArray();
        return await SubjectNamesAsync(ciIds, categoryIds, cancellationToken);
    }

    internal async Task<SubjectNames> SubjectNamesAsync(
        IReadOnlyCollection<Guid> ciIds,
        IReadOnlyCollection<Guid> categoryIds,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<CiSummary> cis = ciIds.Count == 0
            ? []
            : await ciDirectory.GetSummariesAsync(ciIds, cancellationToken);
        Dictionary<Guid, string> categories = categoryIds.Count == 0
            ? []
            : await dbContext.TicketCategories.AsNoTracking()
                .Where(category => categoryIds.Contains(category.Id))
                .Select(category => new { category.Id, category.Name })
                .ToDictionaryAsync(row => row.Id, row => row.Name, cancellationToken);
        return new SubjectNames(
            cis.ToDictionary(ci => ci.Id, ci => (ci.Name, ci.Type)),
            categories);
    }

    internal static ProblemSubjectResponse? Subject(Problem problem, SubjectNames names)
    {
        if (problem.CiId is { } ciId)
        {
            // Null-named rather than absent when the CI is gone: a problem outlives the thing it was
            // about, and "a configuration item that no longer exists" is a truer answer than silence.
            var found = names.Cis.TryGetValue(ciId, out var ci);
            return new ProblemSubjectResponse(
                ProblemSuggestionScope.Ci, ciId, found ? ci.Name : null, found ? ci.Type : null);
        }

        return problem.CategoryId is { } categoryId
            ? new ProblemSubjectResponse(
                ProblemSuggestionScope.Category,
                categoryId,
                names.Categories.GetValueOrDefault(categoryId),
                null)
            : null;
    }

    internal static ProblemResponse Map(
        Problem problem,
        SubjectNames subjects,
        int incidentCount,
        IReadOnlyList<ProblemIncidentResponse>? incidents = null) => new(
        problem.Id,
        problem.Number,
        problem.Title,
        problem.Description,
        problem.Status,
        problem.Priority,
        problem.Status == ProblemStatus.KnownError,
        Subject(problem, subjects),
        problem.RootCause,
        problem.Workaround,
        problem.Resolution,
        problem.AssignedTechnicianId,
        problem.OpenedById,
        problem.OpenedByName,
        incidentCount,
        problem.CreatedAt,
        problem.UpdatedAt,
        problem.KnownErrorAt,
        problem.ResolvedAt,
        problem.ClosedAt,
        incidents);

    /// <summary>
    /// A problem is about one thing or one kind of thing, never both.
    /// <para>
    /// The alternative — allowing both — reads as an intersection ("email tickets about this server") that
    /// nothing in the detector produces and nothing in the list filters for, so it would be a state the
    /// database could hold and no screen could explain.
    /// </para>
    /// </summary>
    private async Task<IReadOnlyDictionary<string, string[]>?> SubjectErrorsAsync(
        Guid? ciId,
        Guid? categoryId,
        CancellationToken cancellationToken)
    {
        if (ciId is not null && categoryId is not null)
        {
            return Field(
                nameof(CreateProblemRequest.CiId),
                "A problem is about a configuration item or about a category, not both.");
        }

        if (categoryId is { } id
            && !await dbContext.TicketCategories.AnyAsync(category => category.Id == id, cancellationToken))
        {
            return Field(nameof(CreateProblemRequest.CategoryId), "That category does not exist.");
        }

        // The CI is deliberately not checked for existence. Helpdesk cannot join to assets, and a lookup
        // through the port would turn every problem write into a cross-module call to reject a typo the
        // browser cannot make — the CI id arrives from a picker or from the detector, both of which read
        // it out of the CMDB moments earlier.
        return null;
    }

    private static IReadOnlyDictionary<string, string[]> Field(string name, string message) =>
        new Dictionary<string, string[]> { [name] = [message] };

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    internal static string ActorId(ClaimsPrincipal actor) =>
        ActorRoles.ActorId(actor)
        ?? throw new InvalidOperationException("An authenticated actor identifier is required.");

    internal static string ActorDisplayName(ClaimsPrincipal actor) =>
        actor.FindFirst("name")?.Value
        ?? actor.Identity?.Name
        ?? actor.FindFirst("preferred_username")?.Value
        ?? ActorId(actor);
}

/// <summary>Names for the CIs and categories a set of problems or suggestions points at, read once.</summary>
public sealed record SubjectNames(
    IReadOnlyDictionary<Guid, (string Name, string Type)> Cis,
    IReadOnlyDictionary<Guid, string> Categories);
