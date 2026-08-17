using System.Security.Claims;

namespace Platform.Actors;

/// <summary>
/// Who is asking, in one place, for the reads that an end user and an operator both reach.
/// <para>
/// Extracted from WP-5.4's <c>SearchVisibility</c> when WP-5.5's widgets needed the same question answered.
/// A second copy would have been a second answer to "is this an operator" — the one kind of duplication
/// that is not merely untidy: two definitions of who counts as staff eventually disagree, and the
/// disagreement is a disclosure rather than a rendering bug. The same reasoning WP-5.2 applied when it
/// pulled <c>SlaClock</c> out of <c>SlaService</c> rather than let a blast radius carry its own arithmetic.
/// </para>
/// <para>
/// Endpoint-level policies still exist and still come first (ARCHITECTURE §6). This is for the reads that
/// cannot be answered by a policy because the answer differs per source: global search returns an end
/// user's own tickets and nothing else, and a dashboard shows an operator five widgets and an end user
/// none.
/// </para>
/// </summary>
public static class ActorRoles
{
    /// <summary>
    /// The three roles the agent app is for, matching what <c>ProtectedRoute</c> gates every agent page in
    /// the SPA with.
    /// </summary>
    private static readonly string[] AgentRoles = ["Admin", "Technician", "Manager"];

    /// <summary>
    /// True for an operator. Deliberately not "is not an end user": a principal with no roles at all is a
    /// token this platform has nothing for, and it must fall on the restrictive side of the question.
    /// </summary>
    public static bool IsAgent(ClaimsPrincipal actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return AgentRoles.Any(actor.IsInRole);
    }

    /// <summary>
    /// True for a manager, which is the only role distinction the platform draws <em>between</em> operators.
    /// WP-5.5 uses it to decide which default dashboard somebody opens on — never what they may read.
    /// </summary>
    public static bool IsManager(ClaimsPrincipal actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return actor.IsInRole("Manager");
    }

    /// <summary>
    /// The immutable identity of whoever is asking, which is what an end user's own records are filtered by
    /// and what a saved dashboard layout belongs to. Never a display name — those repeat and change.
    /// </summary>
    public static string? ActorId(ClaimsPrincipal actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return actor.FindFirstValue("sub") ?? actor.FindFirstValue(ClaimTypes.NameIdentifier);
    }
}
