using System.Security.Claims;

using Modules.Assets.Features.Cis;

namespace Modules.Assets.Features.Contracts;

public interface IVendorService
{
    Task<VendorPageResponse> ListAsync(VendorListRequest request, CancellationToken cancellationToken);

    Task<VendorResponse?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<VendorResult> CreateAsync(CreateVendorRequest request, ClaimsPrincipal actor, CancellationToken cancellationToken);

    Task<VendorResult> UpdateAsync(
        Guid id,
        UpdateVendorRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken);

    Task<ContractOutcome> DeleteAsync(Guid id, ClaimsPrincipal actor, CancellationToken cancellationToken);
}

public interface IContractService
{
    Task<ContractPageResponse> ListAsync(ContractListRequest request, CancellationToken cancellationToken);

    Task<ContractResponse?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<ContractResult> CreateAsync(
        CreateContractRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken);

    Task<ContractResult> UpdateAsync(
        Guid id,
        UpdateContractRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken);

    Task<ContractOutcome> DeleteAsync(Guid id, ClaimsPrincipal actor, CancellationToken cancellationToken);

    /// <summary>Sets what covers one CI: its contract, purchase date and warranty end.</summary>
    Task<CiResult> SetCoverageAsync(
        Guid ciId,
        SetCiCoverageRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken);
}

public interface IContractExpiryService
{
    /// <summary>
    /// Raises every renewal/expiry notification due today. Idempotent: running it twice on the same
    /// day raises nothing the second time.
    /// </summary>
    Task<ContractExpiryRunResponse> RunAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<ContractNotificationResponse>> ListNotificationsAsync(
        int limit,
        CancellationToken cancellationToken);
}
