using System.Security.Claims;

using Microsoft.EntityFrameworkCore;

using Platform.Data;

namespace Platform.Search;

/// <summary>
/// People, from <c>platform.user_profiles</c>. Platform's own table, so this sits beside
/// <see cref="Directory.DirectoryService"/> rather than in a module — no module may query it (ARCHITECTURE §3).
/// </summary>
public sealed class UserSearchSource(PlatformDbContext dbContext) : ISearchSource
{
    public SearchResultType Type => SearchResultType.User;

    /// <summary>
    /// Agents only. The directory is already an agent surface (WP-2.9's <c>/api/directory/users</c> is
    /// behind an operator policy), and a search box that let an end user page through everybody's name,
    /// username and email would be a worse leak than the page it copied.
    /// </summary>
    public bool IsVisibleTo(ClaimsPrincipal actor) => SearchVisibility.IsAgent(actor);

    public async Task<SearchSourceResult> SearchAsync(
        SearchQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        // A username and an email address are quoted verbatim, and the full-text parser keeps an address as
        // one lexeme — so "firstname.lastname" splits into two prefix terms and matches nothing. Both are
        // therefore matched as prefixes of the column as well. See SearchTerm.ToIdentifier.
        var identifier = SearchTerm.ToIdentifier(query.Term);
        var pattern = identifier is null ? null : SearchTerm.EscapeLike(identifier) + "%";

        var users = dbContext.UserProfiles.Where(user =>
            user.SearchVector.Matches(EF.Functions.ToTsQuery(SearchTerm.Configuration, query.TsQuery))
            || (pattern != null && EF.Functions.ILike(user.Username, pattern))
            || (pattern != null && EF.Functions.ILike(user.Email, pattern)));

        var total = await users.CountAsync(cancellationToken);
        var hits = await users
            .OrderByDescending(user =>
                user.SearchVector.Rank(EF.Functions.ToTsQuery(SearchTerm.Configuration, query.TsQuery)))
            .ThenBy(user => user.DisplayName)
            .Take(query.Limit)
            .Select(user => new SearchHit(
                SearchResultType.User,
                user.Id,
                user.DisplayName,
                user.Username,
                // Where they sit, which is what tells two people of the same name apart.
                user.Department!.Name + " · " + user.Site!.Name,
                user.Role))
            .ToListAsync(cancellationToken);

        return new SearchSourceResult(hits, total);
    }
}
