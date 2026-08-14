namespace Modules.Assets.Features.Timeline;

public interface ICiTimelineService
{
    /// <summary>
    /// One CI's interleaved history, or null when no CI answers to that id — which is a fact about the
    /// request and must not read as an asset that nothing has ever happened to.
    /// </summary>
    Task<CiTimelineResponse?> GetTimelineAsync(
        Guid ciId,
        CiTimelineRequest request,
        CancellationToken cancellationToken);
}
