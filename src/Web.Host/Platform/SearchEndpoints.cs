using System.Security.Claims;

using Platform.Search;

namespace Web.Host.Platform;

/// <summary>
/// One search across every module (WP-5.4).
/// <para>
/// Behind plain authentication rather than an operator policy, which makes it the first read in the
/// platform that an end user and an operator both call. That is deliberate and it is what the WP asks for:
/// an end user must be able to find their own tickets, and every other source refuses them by itself. A
/// policy here would have had to be either <c>CanManageTickets</c> — locking out the one caller the WP
/// names — or nothing, which is what this is. Each <see cref="ISearchSource"/> is where the real rule lives.
/// </para>
/// </summary>
public static class SearchEndpoints
{
    public static IEndpointRouteBuilder MapSearchEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/search", async (
            string? q,
            string? types,
            int? limit,
            ISearchService search,
            ClaimsPrincipal actor,
            CancellationToken cancellationToken) =>
        {
            var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

            // A term with nothing searchable in it is refused rather than run. An empty tsquery matches
            // nothing, so the alternative is a 200 holding five empty groups — which reads as "the estate
            // has nothing like that in it" when what actually happened is that nothing was asked.
            if (SearchTerm.ToPrefixTsQuery(q) is null)
            {
                errors["q"] = ["Enter a word or number to search for."];
            }

            // Refused rather than ignored, following the CI timeline: a clamped limit still answers the
            // question that was asked, while a silently dropped type filter answers a different one and
            // looks exactly like a filter that does not work.
            var requested = new List<SearchResultType>();
            if (!string.IsNullOrWhiteSpace(types))
            {
                foreach (var token in types.Split(',', StringSplitOptions.RemoveEmptyEntries
                    | StringSplitOptions.TrimEntries))
                {
                    // `IsDefined` as well as `TryParse`, because `TryParse` accepts any integer: `?types=99`
                    // would otherwise parse to a kind that does not exist, match no source, and answer with
                    // an empty result and no complaint. The same hole WP-5.3 found and fixed.
                    if (Enum.TryParse<SearchResultType>(token, ignoreCase: true, out var type)
                        && Enum.IsDefined(type))
                    {
                        requested.Add(type);
                    }
                    else
                    {
                        errors["types"] =
                        [
                            $"'{token}' is not a searchable kind. Use {string.Join(", ", SearchService.AllTypes)}.",
                        ];
                        break;
                    }
                }
            }

            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var response = await search.SearchAsync(
                new SearchRequest(q!, requested, limit ?? SearchService.DefaultLimit),
                actor,
                cancellationToken);

            return Results.Ok(response);
        }).RequireAuthorization();

        return endpoints;
    }
}
