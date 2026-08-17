using System.Security.Claims;

using Platform.Actors;

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
/// <para>
/// <b>The rule itself now lives in <see cref="ActorRoles"/> and this delegates to it.</b> WP-5.5's widgets
/// ask exactly the same question of exactly the same principal, and two definitions of who counts as an
/// operator would eventually disagree — a disagreement that is a disclosure rather than a rendering bug.
/// These members are kept because they are what the five sources call.
/// </para>
/// </summary>
public static class SearchVisibility
{
    /// <summary>
    /// True for an operator. Deliberately not "is not an end user": a principal with no roles at all is a
    /// token this platform has nothing for, and it must fall on the restrictive side of the question.
    /// </summary>
    public static bool IsAgent(ClaimsPrincipal actor) => ActorRoles.IsAgent(actor);

    /// <summary>
    /// The immutable identity of whoever is searching, which is what an end user's own records are filtered
    /// by. Never a display name — those repeat and change.
    /// </summary>
    public static string? ActorId(ClaimsPrincipal actor) => ActorRoles.ActorId(actor);
}
