using System.Security.Claims;

namespace Platform.Search;

/// <summary>
/// Who may search what, in one place, because five sources answering the same question separately is five
/// chances to answer it differently.
/// <para>
/// Global search is the first read in the platform that an end user and an operator both call, so it is the
/// first that has to decide per source rather than per endpoint. Everything WP-5.1 to WP-5.3 added sits
/// behind <c>CanManageAssets</c> and shows whoever gets in everything it finds; this endpoint is behind
/// plain authentication, and each source narrows itself instead (ARCHITECTURE §6: enforced in queries, not
/// in the UI).
/// </para>
/// </summary>
public static class SearchVisibility
{
    /// <summary>
    /// The three roles the agent app is for. An actor holding any of them searches as an operator, which
    /// matches how <c>ProtectedRoute</c> gates every page in the SPA that these sources feed.
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
    /// The immutable identity of whoever is searching, which is what an end user's own records are filtered
    /// by. Never a display name — those repeat and change.
    /// </summary>
    public static string? ActorId(ClaimsPrincipal actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return actor.FindFirstValue("sub") ?? actor.FindFirstValue(ClaimTypes.NameIdentifier);
    }
}
