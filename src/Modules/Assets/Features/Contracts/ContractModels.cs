using Modules.Assets.Data;

namespace Modules.Assets.Features.Contracts;

public sealed record CreateVendorRequest(
    string Name,
    string? ContactName = null,
    string? ContactEmail = null,
    string? ContactPhone = null,
    string? Website = null,
    string? Notes = null);

public sealed record UpdateVendorRequest(
    string Name,
    string? ContactName = null,
    string? ContactEmail = null,
    string? ContactPhone = null,
    string? Website = null,
    string? Notes = null,
    bool IsActive = true);

public sealed record VendorListRequest(
    string? Search = null,
    bool? IsActive = null,
    int Page = 1,
    int PageSize = 25);

public sealed record VendorResponse(
    Guid Id,
    string Name,
    string? ContactName,
    string? ContactEmail,
    string? ContactPhone,
    string? Website,
    string? Notes,
    bool IsActive,
    int ContractCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record VendorPageResponse(
    IReadOnlyList<VendorResponse> Items,
    int Total,
    int Page,
    int PageSize);

public sealed record CreateContractRequest(
    Guid VendorId,
    string PoNumber,
    string Name,
    ContractType Type,
    DateOnly StartDate,
    DateOnly EndDate,
    bool AutoRenews = false,
    decimal? Cost = null,
    string? Currency = null,
    Guid? OwnerUserId = null,
    Guid? DepartmentId = null,
    string? ContractNumber = null,
    string? Notes = null);

public sealed record UpdateContractRequest(
    Guid VendorId,
    string PoNumber,
    string Name,
    ContractType Type,
    DateOnly StartDate,
    DateOnly EndDate,
    bool AutoRenews = false,
    decimal? Cost = null,
    string? Currency = null,
    Guid? OwnerUserId = null,
    Guid? DepartmentId = null,
    string? ContractNumber = null,
    string? Notes = null,
    bool IsActive = true);

public sealed record ContractListRequest(
    string? Search = null,
    Guid? VendorId = null,
    Guid? DepartmentId = null,
    ContractExpiryStatus? Status = null,
    ContractType? Type = null,
    bool? IsActive = null,
    int Page = 1,
    int PageSize = 25);

public sealed record ContractResponse(
    Guid Id,
    Guid VendorId,
    string VendorName,
    string PoNumber,
    string Name,
    ContractType Type,
    DateOnly StartDate,
    DateOnly EndDate,
    bool AutoRenews,
    decimal? Cost,
    string? Currency,
    Guid? OwnerUserId,
    string? OwnerName,
    string? OwnerEmail,
    Guid? DepartmentId,
    string? DepartmentName,
    string? ContractNumber,
    string? Notes,
    bool IsActive,
    ContractExpiryStatus Status,
    int DaysRemaining,
    int CoveredCiCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record ContractPageResponse(
    IReadOnlyList<ContractResponse> Items,
    int Total,
    int Page,
    int PageSize);

/// <summary>
/// A complete statement of what covers a CI, following the WP-2.2 assignment endpoint: an omitted
/// contract means "covered by nothing", not "leave whatever is there".
/// </summary>
public sealed record SetCiCoverageRequest(
    Guid? ContractId = null,
    DateOnly? PurchaseDate = null,
    DateOnly? WarrantyExpiresAt = null);

/// <summary>
/// What a CI is covered by. Contract fields are read live through the relationship rather than
/// snapshotted, so renaming a contract reaches every CI it covers at once (the WP-2.4 rule).
/// </summary>
public sealed record CiCoverage(
    Guid? ContractId,
    string? ContractName,
    string? PoNumber,
    string? VendorName,
    DateOnly? ContractEndDate,
    DateOnly? PurchaseDate,
    DateOnly? WarrantyExpiresAt,
    ContractExpiryStatus? WarrantyStatus,
    int? WarrantyDaysRemaining);

public sealed record ContractNotificationResponse(
    Guid Id,
    ContractNotificationSubject Subject,
    Guid SubjectId,
    string SubjectName,
    DateOnly DueDate,
    int ThresholdDays,
    string Recipient,
    string Message,
    DateTimeOffset SentAt);

/// <summary>What one pass of the expiry job did. Returned by the manual trigger so a run is verifiable.</summary>
public sealed record ContractExpiryRunResponse(
    DateOnly RunDate,
    int ContractsScanned,
    int WarrantiesScanned,
    IReadOnlyList<ContractNotificationResponse> Raised,
    // WP-4.4 added a third dated thing to the same pass: a licence pool expires the way an agreement
    // does, on the same 30/7/0 thresholds, and duplicating the planner for it would have been two
    // copies of one rule.
    int LicensePoolsScanned = 0);

public enum ContractOutcome
{
    Success,
    NotFound,
    Invalid,
    Duplicate,
    InUse,
    Disposed,
}

public sealed record VendorResult(
    ContractOutcome Outcome,
    VendorResponse? Vendor = null,
    IReadOnlyDictionary<string, string[]>? Errors = null,
    string? Error = null);

public sealed record ContractResult(
    ContractOutcome Outcome,
    ContractResponse? Contract = null,
    IReadOnlyDictionary<string, string[]>? Errors = null,
    string? Error = null);
