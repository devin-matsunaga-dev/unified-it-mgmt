using System.Security.Claims;

using Modules.Helpdesk.Data;

namespace Modules.Helpdesk.Features.Problems;

/// <summary>
/// The recurrence inbox: what the nightly pass noticed, and the two answers a human can give it.
/// </summary>
public interface IProblemSuggestionService
{
    Task<IReadOnlyList<ProblemSuggestionResponse>> ListAsync(
        ProblemSuggestionStatus? status,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken);

    /// <summary>
    /// Counts incidents per CI and per category across the window and writes a suggestion for each
    /// recurrence nobody is already dealing with.
    /// <para>
    /// Idempotent by construction: a second run minutes later finds its own suggestions open and skips
    /// every subject it just raised. That is what lets the job start at host start-up rather than waiting
    /// for the small hours, and what makes the manual run safe to press twice.
    /// </para>
    /// </summary>
    Task<ProblemDetectionRunResponse> DetectAsync(ClaimsPrincipal actor, CancellationToken cancellationToken);

    /// <summary>
    /// Turns a suggestion into a problem and attaches the incidents behind it. The WP's "create problem →
    /// incidents linked", in one act rather than as a create followed by a dozen links.
    /// </summary>
    Task<ProblemSuggestionResult> AcceptAsync(
        Guid id,
        AcceptProblemSuggestionRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken);

    Task<ProblemSuggestionResult> DismissAsync(
        Guid id,
        DismissProblemSuggestionRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken);
}
