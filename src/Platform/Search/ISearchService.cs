using System.Security.Claims;

namespace Platform.Search;

public interface ISearchService
{
    Task<SearchResponse> SearchAsync(
        SearchRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken);
}
