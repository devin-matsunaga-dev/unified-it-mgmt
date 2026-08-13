using Modules.Assets.Data;
using Modules.Assets.Features.Contracts;

namespace Modules.Assets.Features.Software;

public enum SoftwareOutcome
{
    Success = 1,
    NotFound = 2,
    Duplicate = 3,
    InUse = 4,
    Invalid = 5,
}

// ---- Catalogue: products ----------------------------------------------------------------------

public sealed record SoftwareProductResponse(
    Guid Id,
    string Name,
    string Publisher,
    string? Category,
    string? Notes,
    bool IsActive,
    int RuleCount,
    int LicensePoolCount,
    int InstallCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record SoftwareProductPageResponse(
    IReadOnlyList<SoftwareProductResponse> Items,
    int Total,
    int Page,
    int PageSize);

public sealed record SoftwareProductListRequest(
    string? Search,
    bool? IsActive,
    int Page = 1,
    int PageSize = 25);

public sealed record CreateSoftwareProductRequest(
    string Name,
    string Publisher,
    string? Category,
    string? Notes);

public sealed record UpdateSoftwareProductRequest(
    string Name,
    string Publisher,
    string? Category,
    string? Notes,
    bool IsActive);

public sealed record SoftwareProductResult(
    SoftwareOutcome Outcome,
    SoftwareProductResponse? Product = null,
    string? Error = null);

// ---- Catalogue: normalisation rules -----------------------------------------------------------

public sealed record SoftwareRuleResponse(
    Guid Id,
    Guid ProductId,
    string ProductName,
    string Publisher,
    SoftwareMatchKind MatchKind,
    string Pattern,
    int Priority,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record CreateSoftwareRuleRequest(
    Guid ProductId,
    SoftwareMatchKind MatchKind,
    string Pattern,
    int Priority);

public sealed record UpdateSoftwareRuleRequest(
    Guid ProductId,
    SoftwareMatchKind MatchKind,
    string Pattern,
    int Priority,
    bool IsActive);

public sealed record SoftwareRuleResult(
    SoftwareOutcome Outcome,
    SoftwareRuleResponse? Rule = null,
    string? Error = null);

/// <summary>What a re-normalisation pass changed, so adding a rule reports its effect on the history.</summary>
public sealed record SoftwareNormalisationRunResponse(
    int InstallsExamined,
    int Normalised,
    int Renormalised,
    int Unrecognised);

// ---- Installed software -----------------------------------------------------------------------

public sealed record InstalledSoftwareResponse(
    Guid Id,
    Guid CiId,
    string CiName,
    string RawName,
    string? RawPublisher,
    string? Version,
    Guid? ProductId,
    string? ProductName,
    string? ProductPublisher,
    DateOnly? InstalledOn,
    string Source,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt,
    int SightingCount);

public sealed record InstalledSoftwarePageResponse(
    IReadOnlyList<InstalledSoftwareResponse> Items,
    int Total,
    int Page,
    int PageSize);

public sealed record InstalledSoftwareListRequest(
    Guid? CiId,
    Guid? ProductId,
    bool? IsNormalised,
    string? Search,
    int Page = 1,
    int PageSize = 25);

/// <summary>A raw name nothing in the catalogue recognises, with how widespread it is.</summary>
public sealed record UnrecognisedSoftwareResponse(string RawName, string? RawPublisher, int InstallCount, int CiCount);

// ---- Licence pools ----------------------------------------------------------------------------

public sealed record LicensePoolResponse(
    Guid Id,
    Guid ProductId,
    string ProductName,
    string Publisher,
    string Name,
    string? Reference,
    int Entitlements,
    DateOnly? PurchaseDate,
    DateOnly? ExpiresAt,
    string? Notes,
    bool IsActive,
    // Null for a perpetual licence: no end date is no status, not "Active forever".
    ContractExpiryStatus? Status,
    int? DaysRemaining,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record LicensePoolPageResponse(
    IReadOnlyList<LicensePoolResponse> Items,
    int Total,
    int Page,
    int PageSize);

public sealed record LicensePoolListRequest(
    string? Search,
    Guid? ProductId,
    ContractExpiryStatus? Status,
    bool? IsActive,
    int Page = 1,
    int PageSize = 25);

public sealed record CreateLicensePoolRequest(
    Guid ProductId,
    string Name,
    string? Reference,
    int Entitlements,
    DateOnly? PurchaseDate,
    DateOnly? ExpiresAt,
    string? Notes);

public sealed record UpdateLicensePoolRequest(
    Guid ProductId,
    string Name,
    string? Reference,
    int Entitlements,
    DateOnly? PurchaseDate,
    DateOnly? ExpiresAt,
    string? Notes,
    bool IsActive);

public sealed record LicensePoolResult(
    SoftwareOutcome Outcome,
    LicensePoolResponse? Pool = null,
    string? Error = null,
    IReadOnlyDictionary<string, string[]>? Errors = null);

// ---- Compliance -------------------------------------------------------------------------------

/// <summary>Where a product stands against what has been bought for it.</summary>
public enum SoftwareComplianceState
{
    /// <summary>Installed on no more devices than the live pools entitle.</summary>
    Compliant = 1,

    /// <summary>Pools exist and are outnumbered by the installs.</summary>
    OverDeployed = 2,

    /// <summary>Installed somewhere, entitled by nothing: no pool has ever been recorded for it.</summary>
    Unlicensed = 3,

    /// <summary>Entitlements nobody is using — the only state that is about money rather than risk.</summary>
    Unused = 4,
}

public sealed record SoftwareComplianceRowResponse(
    Guid ProductId,
    string ProductName,
    string Publisher,
    string? Category,
    int InstalledCiCount,
    int InstallCount,
    int Entitled,
    int LicensePoolCount,
    int ExpiredPoolCount,
    // Positive when over-deployed, negative when there are entitlements to spare.
    int Overage,
    SoftwareComplianceState State,
    DateOnly? NextExpiry,
    ContractExpiryStatus? ExpiryStatus);

public sealed record SoftwareComplianceResponse(
    DateOnly GeneratedOn,
    int ProductCount,
    int OverDeployedCount,
    int UnlicensedCount,
    int TotalInstalls,
    int TotalEntitled,
    IReadOnlyList<SoftwareComplianceRowResponse> Rows);

public sealed record SoftwareComplianceRequest(SoftwareComplianceState? State, string? Search);

/// <summary>What one compliance pass found and raised. Mirrors <c>ContractExpiryRunResponse</c>.</summary>
public sealed record SoftwareComplianceRunResponse(
    DateOnly Today,
    int ProductsChecked,
    int OverDeployed,
    IReadOnlyList<ContractNotificationResponse> Raised);

// ---- Import -----------------------------------------------------------------------------------

public enum SoftwareImportOutcome
{
    Success = 1,
    InvalidFile = 2,
}

public enum SoftwareImportAction
{
    Create = 1,
    Update = 2,
    Error = 3,
}

public sealed record SoftwareImportRowResult(
    int LineNumber,
    SoftwareImportAction Action,
    string? Machine,
    string? SoftwareName,
    string? Version,
    Guid? CiId,
    string? CiName,
    Guid? ProductId,
    string? ProductName,
    IReadOnlyList<string> Errors);

/// <summary>The dry run and the commit return the same shape, so the preview is literally what happened.</summary>
public sealed record SoftwareImportReport(
    bool IsDryRun,
    string FileName,
    int TotalRows,
    int Created,
    int Updated,
    int Failed,
    int MachinesMatched,
    int Normalised,
    int Unrecognised,
    IReadOnlyList<SoftwareImportRowResult> Rows,
    IReadOnlyList<string> UnrecognisedNames);

public sealed record SoftwareImportResult(
    SoftwareImportOutcome Outcome,
    SoftwareImportReport? Report = null,
    string? Error = null);
