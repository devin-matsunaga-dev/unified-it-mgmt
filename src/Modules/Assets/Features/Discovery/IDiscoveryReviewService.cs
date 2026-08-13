using System.Security.Claims;

using Contracts.Events;

namespace Modules.Assets.Features.Discovery;

/// <summary>
/// The Assets module's public surface for what scans have found: the intake that consumes
/// <see cref="DeviceDiscovered"/>, the review queue an operator works, and the discovery facts a CI
/// page renders beside what the CMDB records.
/// </summary>
public interface IDiscoveryReviewService
{
    /// <summary>
    /// Place one discovery: match it to a CI and refresh that CI's facts, or file it for review.
    /// <para>
    /// Called by <see cref="DeviceDiscoveredConsumer"/> and by nothing else. It takes the contract event
    /// directly rather than a mapped shape so that the test which reads WP-4.1's committed envelope
    /// fixture drives the same code path a broker does.
    /// </para>
    /// </summary>
    Task<DiscoveryIntakeResult> IngestAsync(DeviceDiscovered discovery, CancellationToken cancellationToken);

    Task<DiscoveredDevicePageResponse> ListAsync(
        DiscoveredDeviceListRequest request,
        CancellationToken cancellationToken);

    Task<DiscoveredDeviceResponse?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<DiscoveryReviewResult> ApproveAsync(
        Guid id,
        ApproveDiscoveredDeviceRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken);

    Task<DiscoveryReviewResult> RejectAsync(
        Guid id,
        RejectDiscoveredDeviceRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken);

    Task<CiDiscoveryFactsResponse?> GetFactsAsync(Guid ciId, CancellationToken cancellationToken);
}
