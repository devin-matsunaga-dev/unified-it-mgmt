using System.Security.Claims;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Modules.Helpdesk.Data;
using Platform.Actors;
using Platform.Auditing;

namespace Modules.Helpdesk.Features.Problems;

public sealed class ProblemSuggestionService(
    HelpdeskDbContext dbContext,
    ProblemService problems,
    IAuditService auditService,
    IOptions<ProblemDetectionOptions> options,
    ILogger<ProblemSuggestionService> logger) : IProblemSuggestionService
{
    public async Task<IReadOnlyList<ProblemSuggestionResponse>> ListAsync(
        ProblemSuggestionStatus? status,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        if (!ActorRoles.IsAgent(actor))
        {
            return [];
        }

        var query = dbContext.ProblemSuggestions.AsNoTracking().AsQueryable();
        if (status is { } wanted)
        {
            query = query.Where(suggestion => suggestion.Status == wanted);
        }

        var suggestions = await query
            .OrderByDescending(suggestion => suggestion.IncidentCount)
            .ThenByDescending(suggestion => suggestion.DetectedAt)
            .ToListAsync(cancellationToken);
        return await MapManyAsync(suggestions, cancellationToken);
    }

    public async Task<ProblemDetectionRunResponse> DetectAsync(
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var windowEnd = DateTimeOffset.UtcNow;
        var windowStart = windowEnd - TimeSpan.FromDays(settings.WindowDays);

        var candidates = await CountAsync(windowStart, windowEnd, cancellationToken);
        var states = await StatesAsync(candidates, cancellationToken);
        var verdicts = RecurrenceDetector.Decide(candidates, states, settings, windowEnd);

        var raised = new List<ProblemSuggestion>();
        foreach (var verdict in verdicts.Where(verdict => verdict.Decision == RecurrenceDecision.Suggest))
        {
            var candidate = verdict.Candidate;
            raised.Add(new ProblemSuggestion
            {
                Id = Guid.CreateVersion7(),
                Scope = candidate.Scope,
                CiId = candidate.Scope == ProblemSuggestionScope.Ci ? candidate.SubjectId : null,
                CategoryId = candidate.Scope == ProblemSuggestionScope.Category ? candidate.SubjectId : null,
                IncidentCount = candidate.IncidentCount,
                WindowStart = windowStart,
                WindowEnd = windowEnd,
                Status = ProblemSuggestionStatus.Open,
                DetectedAt = windowEnd,
            });
        }

        if (raised.Count > 0)
        {
            dbContext.ProblemSuggestions.AddRange(raised);
            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException exception)
            {
                // The filtered unique index refusing a second open suggestion for a subject. It can only
                // happen when two passes overlap — the job is [DisallowConcurrentExecution] but the manual
                // run is not gated against it — and the index winning is the correct outcome, so the pass
                // reports having raised nothing rather than failing the caller.
                logger.LogInformation(
                    exception,
                    "A concurrent detection pass had already raised one of these {Count} suggestions; none were written.",
                    raised.Count);
                foreach (var entry in dbContext.ChangeTracker.Entries<ProblemSuggestion>().ToList())
                {
                    entry.State = EntityState.Detached;
                }

                raised.Clear();
            }
        }

        foreach (var suggestion in raised)
        {
            await auditService.WriteAsync(
                actor,
                "Suggested",
                "ProblemSuggestion",
                suggestion.Id.ToString(),
                null,
                new
                {
                    suggestion.Scope,
                    suggestion.CiId,
                    suggestion.CategoryId,
                    suggestion.IncidentCount,
                    suggestion.WindowStart,
                    suggestion.WindowEnd,
                },
                cancellationToken);
        }

        var skipped = verdicts
            .Where(verdict => verdict.Decision != RecurrenceDecision.Suggest)
            .GroupBy(verdict => verdict.Decision)
            .ToDictionary(group => group.Key.ToString(), group => group.Count());

        logger.LogInformation(
            "Problem detection examined {Examined} subjects over {WindowDays} days at a threshold of {Threshold} "
            + "and raised {Raised} suggestions ({Skipped}).",
            candidates.Count,
            settings.WindowDays,
            settings.MinimumIncidents,
            raised.Count,
            string.Join(", ", skipped.Select(entry => $"{entry.Key}: {entry.Value}")));

        return new ProblemDetectionRunResponse(
            windowStart,
            windowEnd,
            settings.MinimumIncidents,
            candidates.Count,
            raised.Count,
            skipped,
            await MapManyAsync(raised, cancellationToken));
    }

    public async Task<ProblemSuggestionResult> AcceptAsync(
        Guid id,
        AcceptProblemSuggestionRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!ActorRoles.IsAgent(actor))
        {
            return new(ProblemOutcome.Forbidden, Error: "Problems are an agent surface.");
        }

        var suggestion = await dbContext.ProblemSuggestions
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (suggestion is null)
        {
            return new(ProblemOutcome.NotFound);
        }

        if (suggestion.Status != ProblemSuggestionStatus.Open)
        {
            return new(
                ProblemOutcome.Duplicate,
                Error: suggestion.Status == ProblemSuggestionStatus.Accepted
                    ? "Somebody has already made a problem of this suggestion."
                    : "This suggestion was dismissed. Raise the problem directly if it should exist.");
        }

        var names = await NamesForAsync([suggestion], cancellationToken);
        var subjectName = SubjectOf(suggestion, names).Name;
        var now = DateTimeOffset.UtcNow;

        var problem = new Problem
        {
            Id = Guid.CreateVersion7(),
            Title = Trimmed(request.Title) ?? DefaultTitle(suggestion, subjectName),
            Description = Trimmed(request.Description) ?? DefaultDescription(suggestion, subjectName),
            Status = ProblemStatus.Investigating,
            Priority = request.Priority ?? TicketPriority.Medium,
            CiId = suggestion.CiId,
            CategoryId = suggestion.CategoryId,
            OpenedById = ProblemService.ActorId(actor),
            OpenedByName = ProblemService.ActorDisplayName(actor),
            CreatedAt = now,
            UpdatedAt = now,
        };
        dbContext.Problems.Add(problem);

        // Every incident on this subject since the window opened, with no upper bound: an incident that
        // arrived while the suggestion sat in the inbox is part of the same recurrence, and a problem that
        // opened already under-counting its own evidence is one nobody trusts.
        var incidentIds = await IncidentsForSubjectAsync(suggestion, cancellationToken);
        var attachable = await problems.AttachableIncidentsAsync(incidentIds, cancellationToken);
        foreach (var ticketId in attachable)
        {
            dbContext.ProblemIncidents.Add(ProblemService.NewLink(problem.Id, ticketId, actor, now));
        }

        suggestion.Status = ProblemSuggestionStatus.Accepted;
        suggestion.CreatedProblemId = problem.Id;
        suggestion.ResolvedById = ProblemService.ActorId(actor);
        suggestion.ResolvedByName = ProblemService.ActorDisplayName(actor);
        suggestion.ResolvedAt = now;

        await dbContext.SaveChangesAsync(cancellationToken);

        var response = await MapAsync(suggestion, cancellationToken);
        await auditService.WriteAsync(
            actor, "Accepted", "ProblemSuggestion", suggestion.Id.ToString(), null,
            new { ProblemId = problem.Id, IncidentsLinked = attachable.Count }, cancellationToken);
        await auditService.WriteAsync(
            actor, "Created", "Problem", problem.Id.ToString(), null,
            await problems.ReadBackAsync(problem.Id, cancellationToken), cancellationToken);
        return new(ProblemOutcome.Success, response);
    }

    public async Task<ProblemSuggestionResult> DismissAsync(
        Guid id,
        DismissProblemSuggestionRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!ActorRoles.IsAgent(actor))
        {
            return new(ProblemOutcome.Forbidden, Error: "Problems are an agent surface.");
        }

        var suggestion = await dbContext.ProblemSuggestions
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (suggestion is null)
        {
            return new(ProblemOutcome.NotFound);
        }

        if (suggestion.Status != ProblemSuggestionStatus.Open)
        {
            return new(ProblemOutcome.Duplicate, Error: "This suggestion has already been answered.");
        }

        var now = DateTimeOffset.UtcNow;
        suggestion.Status = ProblemSuggestionStatus.Dismissed;
        suggestion.ResolvedById = ProblemService.ActorId(actor);
        suggestion.ResolvedByName = ProblemService.ActorDisplayName(actor);
        suggestion.ResolvedAt = now;
        suggestion.DismissReason = Trimmed(request.Reason);
        await dbContext.SaveChangesAsync(cancellationToken);

        var response = await MapAsync(suggestion, cancellationToken);
        await auditService.WriteAsync(
            actor, "Dismissed", "ProblemSuggestion", suggestion.Id.ToString(), null,
            new { suggestion.DismissReason, CooldownDays = options.Value.DismissalCooldownDays }, cancellationToken);
        return new(ProblemOutcome.Success, response);
    }

    // ---- counting ----

    private async Task<IReadOnlyList<RecurrenceCandidate>> CountAsync(
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        CancellationToken cancellationToken)
    {
        // Incidents only. A service request is somebody asking for something, and five people asking for
        // a laptop in a week is a busy month rather than a fault.
        var perCi = await dbContext.TicketCiLinks.AsNoTracking()
            .Where(link => link.Ticket.Type == TicketType.Incident
                && link.Ticket.CreatedAt >= windowStart
                && link.Ticket.CreatedAt <= windowEnd)
            .Select(link => new { link.CiId, link.Ticket.CreatedAt })
            .GroupBy(row => row.CiId)
            .Select(group => new
            {
                SubjectId = group.Key,
                Count = group.Count(),
                First = group.Min(row => row.CreatedAt),
                Last = group.Max(row => row.CreatedAt),
            })
            .ToListAsync(cancellationToken);

        var perCategory = await dbContext.Tickets.AsNoTracking()
            .Where(ticket => ticket.Type == TicketType.Incident
                && ticket.CategoryId != null
                && ticket.CreatedAt >= windowStart
                && ticket.CreatedAt <= windowEnd)
            .Select(ticket => new { CategoryId = ticket.CategoryId!.Value, ticket.CreatedAt })
            .GroupBy(row => row.CategoryId)
            .Select(group => new
            {
                SubjectId = group.Key,
                Count = group.Count(),
                First = group.Min(row => row.CreatedAt),
                Last = group.Max(row => row.CreatedAt),
            })
            .ToListAsync(cancellationToken);

        return
        [
            .. perCi.Select(row => new RecurrenceCandidate(
                ProblemSuggestionScope.Ci, row.SubjectId, row.Count, row.First, row.Last)),
            .. perCategory.Select(row => new RecurrenceCandidate(
                ProblemSuggestionScope.Category, row.SubjectId, row.Count, row.First, row.Last)),
        ];
    }

    private async Task<IReadOnlyDictionary<(ProblemSuggestionScope, Guid), RecurrenceSubjectState>> StatesAsync(
        IReadOnlyCollection<RecurrenceCandidate> candidates,
        CancellationToken cancellationToken)
    {
        var states = new Dictionary<(ProblemSuggestionScope, Guid), RecurrenceSubjectState>();
        if (candidates.Count == 0)
        {
            return states;
        }

        var ciIds = candidates.Where(candidate => candidate.Scope == ProblemSuggestionScope.Ci)
            .Select(candidate => candidate.SubjectId).ToArray();
        var categoryIds = candidates.Where(candidate => candidate.Scope == ProblemSuggestionScope.Category)
            .Select(candidate => candidate.SubjectId).ToArray();

        // Open means somebody is still working it. A problem closed last month does not stop the same
        // switch raising a new suggestion when it starts failing again.
        var openProblems = await dbContext.Problems.AsNoTracking()
            .Where(problem => ProblemStatuses.Open.Contains(problem.Status))
            .Select(problem => new { problem.CiId, problem.CategoryId })
            .ToListAsync(cancellationToken);

        var suggestions = await dbContext.ProblemSuggestions.AsNoTracking()
            .Where(suggestion => suggestion.Status == ProblemSuggestionStatus.Open
                || suggestion.Status == ProblemSuggestionStatus.Dismissed)
            .Select(suggestion => new
            {
                suggestion.Scope,
                suggestion.CiId,
                suggestion.CategoryId,
                suggestion.Status,
                suggestion.ResolvedAt,
            })
            .ToListAsync(cancellationToken);

        foreach (var (scope, ids) in new[]
        {
            (ProblemSuggestionScope.Ci, ciIds),
            (ProblemSuggestionScope.Category, categoryIds),
        })
        {
            foreach (var subjectId in ids)
            {
                var hasProblem = openProblems.Any(problem => scope == ProblemSuggestionScope.Ci
                    ? problem.CiId == subjectId
                    : problem.CategoryId == subjectId);
                var mine = suggestions.Where(suggestion => suggestion.Scope == scope
                    && (scope == ProblemSuggestionScope.Ci
                        ? suggestion.CiId == subjectId
                        : suggestion.CategoryId == subjectId)).ToList();
                states[(scope, subjectId)] = new RecurrenceSubjectState(
                    hasProblem,
                    mine.Any(suggestion => suggestion.Status == ProblemSuggestionStatus.Open),
                    mine.Where(suggestion => suggestion.Status == ProblemSuggestionStatus.Dismissed)
                        .Max(suggestion => suggestion.ResolvedAt));
            }
        }

        return states;
    }

    private async Task<IReadOnlyList<Guid>> IncidentsForSubjectAsync(
        ProblemSuggestion suggestion,
        CancellationToken cancellationToken) =>
        suggestion.Scope == ProblemSuggestionScope.Ci
            ? await dbContext.TicketCiLinks.AsNoTracking()
                .Where(link => link.CiId == suggestion.CiId
                    && link.Ticket.Type == TicketType.Incident
                    && link.Ticket.CreatedAt >= suggestion.WindowStart)
                .Select(link => link.TicketId)
                .ToListAsync(cancellationToken)
            : await dbContext.Tickets.AsNoTracking()
                .Where(ticket => ticket.CategoryId == suggestion.CategoryId
                    && ticket.Type == TicketType.Incident
                    && ticket.CreatedAt >= suggestion.WindowStart)
                .Select(ticket => ticket.Id)
                .ToListAsync(cancellationToken);

    // ---- mapping ----

    private static string DefaultTitle(ProblemSuggestion suggestion, string? subjectName)
    {
        var subject = subjectName
            ?? (suggestion.Scope == ProblemSuggestionScope.Ci ? "one configuration item" : "one category");
        return suggestion.Scope == ProblemSuggestionScope.Ci
            ? $"Recurring incidents on {subject}"
            : $"Recurring {subject} incidents";
    }

    private static string DefaultDescription(ProblemSuggestion suggestion, string? subjectName)
    {
        var subject = suggestion.Scope == ProblemSuggestionScope.Ci
            ? subjectName ?? "a configuration item"
            : $"the {subjectName ?? "unnamed"} category";
        var days = Math.Max(1, (int)Math.Round((suggestion.WindowEnd - suggestion.WindowStart).TotalDays));
        return $"Raised from a recurrence the platform noticed: {suggestion.IncidentCount} incidents about "
            + $"{subject} in {days} day{(days == 1 ? string.Empty : "s")}. The incidents are linked below. "
            + "What they have in common is still to be established.";
    }

    private async Task<ProblemSuggestionResponse> MapAsync(
        ProblemSuggestion suggestion,
        CancellationToken cancellationToken) =>
        (await MapManyAsync([suggestion], cancellationToken))[0];

    private async Task<IReadOnlyList<ProblemSuggestionResponse>> MapManyAsync(
        IReadOnlyList<ProblemSuggestion> suggestions,
        CancellationToken cancellationToken)
    {
        if (suggestions.Count == 0)
        {
            return [];
        }

        var names = await NamesForAsync(suggestions, cancellationToken);
        var problemIds = suggestions.Where(suggestion => suggestion.CreatedProblemId is not null)
            .Select(suggestion => suggestion.CreatedProblemId!.Value).Distinct().ToArray();
        Dictionary<Guid, long> problemNumbers = problemIds.Length == 0
            ? []
            : await dbContext.Problems.AsNoTracking()
                .Where(problem => problemIds.Contains(problem.Id))
                .Select(problem => new { problem.Id, problem.SequenceNumber })
                .ToDictionaryAsync(row => row.Id, row => row.SequenceNumber, cancellationToken);

        return [.. suggestions.Select(suggestion => new ProblemSuggestionResponse(
            suggestion.Id,
            suggestion.Scope,
            SubjectOf(suggestion, names),
            suggestion.IncidentCount,
            suggestion.WindowStart,
            suggestion.WindowEnd,
            suggestion.Status,
            suggestion.DetectedAt,
            suggestion.CreatedProblemId,
            suggestion.CreatedProblemId is { } problemId && problemNumbers.TryGetValue(problemId, out var sequence)
                ? $"PRB-{sequence:000000}"
                : null,
            suggestion.ResolvedById,
            suggestion.ResolvedByName,
            suggestion.ResolvedAt,
            suggestion.DismissReason))];
    }

    private Task<SubjectNames> NamesForAsync(
        IReadOnlyCollection<ProblemSuggestion> suggestions,
        CancellationToken cancellationToken) =>
        problems.SubjectNamesAsync(
            [.. suggestions.Where(item => item.CiId is not null).Select(item => item.CiId!.Value).Distinct()],
            [.. suggestions.Where(item => item.CategoryId is not null).Select(item => item.CategoryId!.Value).Distinct()],
            cancellationToken);

    private static ProblemSubjectResponse SubjectOf(ProblemSuggestion suggestion, SubjectNames names)
    {
        if (suggestion.Scope == ProblemSuggestionScope.Ci)
        {
            var ciId = suggestion.CiId ?? Guid.Empty;
            var found = names.Cis.TryGetValue(ciId, out var ci);
            return new ProblemSubjectResponse(
                ProblemSuggestionScope.Ci, ciId, found ? ci.Name : null, found ? ci.Type : null);
        }

        var categoryId = suggestion.CategoryId ?? Guid.Empty;
        return new ProblemSubjectResponse(
            ProblemSuggestionScope.Category, categoryId, names.Categories.GetValueOrDefault(categoryId), null);
    }

    private static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
