using System.Security.Claims;

using Microsoft.AspNetCore.Http;

namespace Modules.Assets.Features.Software;

/// <summary>The normalisation catalogue: canonical products, the rules that reach them, and what is installed.</summary>
public interface ISoftwareCatalogService
{
    Task<SoftwareProductPageResponse> ListProductsAsync(
        SoftwareProductListRequest request, CancellationToken cancellationToken);

    Task<SoftwareProductResponse?> GetProductAsync(Guid id, CancellationToken cancellationToken);

    Task<SoftwareProductResult> CreateProductAsync(
        CreateSoftwareProductRequest request, ClaimsPrincipal actor, CancellationToken cancellationToken);

    Task<SoftwareProductResult> UpdateProductAsync(
        Guid id, UpdateSoftwareProductRequest request, ClaimsPrincipal actor, CancellationToken cancellationToken);

    Task<SoftwareOutcome> DeleteProductAsync(Guid id, ClaimsPrincipal actor, CancellationToken cancellationToken);

    Task<IReadOnlyList<SoftwareRuleResponse>> ListRulesAsync(Guid? productId, CancellationToken cancellationToken);

    Task<SoftwareRuleResult> CreateRuleAsync(
        CreateSoftwareRuleRequest request, ClaimsPrincipal actor, CancellationToken cancellationToken);

    Task<SoftwareRuleResult> UpdateRuleAsync(
        Guid id, UpdateSoftwareRuleRequest request, ClaimsPrincipal actor, CancellationToken cancellationToken);

    Task<SoftwareOutcome> DeleteRuleAsync(Guid id, ClaimsPrincipal actor, CancellationToken cancellationToken);

    /// <summary>
    /// Re-runs the catalogue over every install already recorded, so a rule added today reaches the
    /// inventory imported last month. Idempotent: a second pass changes nothing.
    /// </summary>
    Task<SoftwareNormalisationRunResponse> NormaliseAsync(ClaimsPrincipal actor, CancellationToken cancellationToken);

    Task<InstalledSoftwarePageResponse> ListInstallsAsync(
        InstalledSoftwareListRequest request, CancellationToken cancellationToken);

    /// <summary>The raw names no rule claims, most widespread first — the catalogue's own to-do list.</summary>
    Task<IReadOnlyList<UnrecognisedSoftwareResponse>> ListUnrecognisedAsync(
        int limit, CancellationToken cancellationToken);
}

/// <summary>Licence pools and the installed-versus-entitled report they are read through.</summary>
public interface ILicensingService
{
    Task<LicensePoolPageResponse> ListPoolsAsync(LicensePoolListRequest request, CancellationToken cancellationToken);

    Task<LicensePoolResponse?> GetPoolAsync(Guid id, CancellationToken cancellationToken);

    Task<LicensePoolResult> CreatePoolAsync(
        CreateLicensePoolRequest request, ClaimsPrincipal actor, CancellationToken cancellationToken);

    Task<LicensePoolResult> UpdatePoolAsync(
        Guid id, UpdateLicensePoolRequest request, ClaimsPrincipal actor, CancellationToken cancellationToken);

    Task<SoftwareOutcome> DeletePoolAsync(Guid id, ClaimsPrincipal actor, CancellationToken cancellationToken);

    Task<SoftwareComplianceResponse> ReportAsync(SoftwareComplianceRequest request, CancellationToken cancellationToken);
}

/// <summary>The over-deployment pass: reads the same report and records a notice for each shortfall.</summary>
public interface ISoftwareComplianceService
{
    Task<SoftwareComplianceRunResponse> RunAsync(CancellationToken cancellationToken);
}

/// <summary>The agentless collection path: an inventory file from an agent, an RMM export or a script.</summary>
public interface ISoftwareImportService
{
    Task<SoftwareImportResult> PreviewAsync(IFormFile file, CancellationToken cancellationToken);

    Task<SoftwareImportResult> CommitAsync(IFormFile file, ClaimsPrincipal actor, CancellationToken cancellationToken);
}
