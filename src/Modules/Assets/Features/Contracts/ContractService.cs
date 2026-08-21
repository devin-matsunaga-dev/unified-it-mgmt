using System.Security.Claims;

using System.Text.RegularExpressions;

using Microsoft.EntityFrameworkCore;
using Modules.Assets.Data;
using Modules.Assets.Features.Cis;
using Platform.Auditing;
using Platform.Directory;

namespace Modules.Assets.Features.Contracts;

public sealed class ContractService(
    AssetsDbContext dbContext,
    IDirectoryService directoryService,
    IAuditService auditService) : IContractService
{
    internal const int MaximumPageSize = 200;

    public async Task<ContractPageResponse> ListAsync(ContractListRequest request, CancellationToken cancellationToken)
    {
        var page = Math.Max(request.Page, 1);
        var pageSize = Math.Clamp(request.PageSize, 1, MaximumPageSize);
        var today = ContractExpiryCalculator.Today();
        var query = dbContext.Contracts.Include(contract => contract.Vendor).AsQueryable();

        if (request.VendorId is not null)
        {
            query = query.Where(contract => contract.VendorId == request.VendorId);
        }

        if (request.DepartmentId is not null)
        {
            query = query.Where(contract => contract.DepartmentId == request.DepartmentId);
        }

        if (request.Type is not null)
        {
            query = query.Where(contract => contract.Type == request.Type);
        }

        if (request.IsActive is not null)
        {
            query = query.Where(contract => contract.IsActive == request.IsActive);
        }

        // Status is a comparison against today rather than a stored column, so it can never drift out
        // of step with the end date.
        var soonBoundary = today.AddDays(30);
        query = request.Status switch
        {
            ContractExpiryStatus.Expired => query.Where(contract => contract.EndDate < today),
            ContractExpiryStatus.ExpiringSoon => query.Where(contract =>
                contract.EndDate >= today && contract.EndDate <= soonBoundary),
            ContractExpiryStatus.Active => query.Where(contract => contract.EndDate > soonBoundary),
            _ => query,
        };

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var term = $"%{request.Search.Trim()}%";
            query = query.Where(contract =>
                EF.Functions.ILike(contract.Name, term)
                || EF.Functions.ILike(contract.PoNumber, term)
                || EF.Functions.ILike(contract.Vendor.Name, term));
        }

        var total = await query.CountAsync(cancellationToken);
        var contracts = await query
            .OrderBy(contract => contract.EndDate).ThenBy(contract => contract.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(contract => new { Contract = contract, CoveredCiCount = contract.Cis.Count })
            .ToListAsync(cancellationToken);
        return new(
            [.. contracts.Select(row => Map(row.Contract, row.CoveredCiCount, today))],
            total,
            page,
            pageSize);
    }

    public async Task<ContractResponse?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var row = await dbContext.Contracts
            .Include(contract => contract.Vendor)
            .Where(contract => contract.Id == id)
            .Select(contract => new { Contract = contract, CoveredCiCount = contract.Cis.Count })
            .SingleOrDefaultAsync(cancellationToken);
        return row is null ? null : Map(row.Contract, row.CoveredCiCount, ContractExpiryCalculator.Today());
    }

    public async Task<ContractResult> CreateAsync(
        CreateContractRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        var vendor = await dbContext.Vendors.SingleOrDefaultAsync(
            item => item.Id == request.VendorId, cancellationToken);
        if (vendor is null)
        {
            return Invalid(nameof(request.VendorId), $"Vendor '{request.VendorId}' does not exist.");
        }

        var number = NormalisePoNumber(request.PoNumber);
        if (await NumberTakenAsync(number, null, cancellationToken))
        {
            return new(ContractOutcome.Duplicate, Error: $"PO number '{number}' is already used.");
        }

        var owner = await ResolveOwnerAsync(request.OwnerUserId, cancellationToken);
        if (owner.Error is { } ownerError)
        {
            return Invalid(nameof(request.OwnerUserId), ownerError);
        }

        var (department, departmentError) = await ResolveDepartmentAsync(request.DepartmentId, cancellationToken);
        if (departmentError is not null)
        {
            return Invalid(nameof(request.DepartmentId), departmentError);
        }

        var now = DateTimeOffset.UtcNow;
        var contract = new Contract
        {
            Id = Guid.CreateVersion7(),
            VendorId = vendor.Id,
            Vendor = vendor,
            PoNumber = number,
            Name = request.Name.Trim(),
            Type = request.Type,
            StartDate = request.StartDate,
            EndDate = request.EndDate,
            AutoRenews = request.AutoRenews,
            Cost = request.Cost,
            Currency = Normalise(request.Currency)?.ToUpperInvariant(),
            OwnerUserId = owner.User?.Id,
            DepartmentId = department?.Id,
            DepartmentName = department?.Name,
            ContractNumber = Normalise(request.ContractNumber),
            OwnerName = owner.User?.DisplayName,
            OwnerEmail = owner.User?.Email,
            Notes = Normalise(request.Notes),
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        dbContext.Contracts.Add(contract);
        await dbContext.SaveChangesAsync(cancellationToken);

        var response = Map(contract, 0, ContractExpiryCalculator.Today());
        await auditService.WriteAsync(
            actor, "Created", "Contract", contract.Id.ToString(), null, response, cancellationToken);
        return new(ContractOutcome.Success, response);
    }

    public async Task<ContractResult> UpdateAsync(
        Guid id,
        UpdateContractRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        var contract = await dbContext.Contracts.Include(item => item.Vendor)
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (contract is null)
        {
            return new(ContractOutcome.NotFound);
        }

        var vendor = await dbContext.Vendors.SingleOrDefaultAsync(
            item => item.Id == request.VendorId, cancellationToken);
        if (vendor is null)
        {
            return Invalid(nameof(request.VendorId), $"Vendor '{request.VendorId}' does not exist.");
        }

        var number = NormalisePoNumber(request.PoNumber);
        if (await NumberTakenAsync(number, id, cancellationToken))
        {
            return new(ContractOutcome.Duplicate, Error: $"PO number '{number}' is already used.");
        }

        var owner = await ResolveOwnerAsync(request.OwnerUserId, cancellationToken);
        if (owner.Error is { } ownerError)
        {
            return Invalid(nameof(request.OwnerUserId), ownerError);
        }

        var (department, departmentError) = await ResolveDepartmentAsync(request.DepartmentId, cancellationToken);
        if (departmentError is not null)
        {
            return Invalid(nameof(request.DepartmentId), departmentError);
        }

        var today = ContractExpiryCalculator.Today();
        var coveredCiCount = await dbContext.Cis.CountAsync(ci => ci.ContractId == id, cancellationToken);
        var before = Map(contract, coveredCiCount, today);
        contract.VendorId = vendor.Id;
        contract.Vendor = vendor;
        contract.PoNumber = number;
        contract.Name = request.Name.Trim();
        contract.Type = request.Type;
        contract.StartDate = request.StartDate;
        contract.EndDate = request.EndDate;
        contract.AutoRenews = request.AutoRenews;
        contract.Cost = request.Cost;
        contract.Currency = Normalise(request.Currency)?.ToUpperInvariant();
        contract.OwnerUserId = owner.User?.Id;
        contract.DepartmentId = department?.Id;
        contract.DepartmentName = department?.Name;
        contract.ContractNumber = Normalise(request.ContractNumber);
        contract.OwnerName = owner.User?.DisplayName;
        contract.OwnerEmail = owner.User?.Email;
        contract.Notes = Normalise(request.Notes);
        contract.IsActive = request.IsActive;
        contract.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);

        var after = Map(contract, coveredCiCount, today);
        await auditService.WriteAsync(actor, "Updated", "Contract", id.ToString(), before, after, cancellationToken);
        return new(ContractOutcome.Success, after);
    }

    public async Task<ContractOutcome> DeleteAsync(Guid id, ClaimsPrincipal actor, CancellationToken cancellationToken)
    {
        var contract = await dbContext.Contracts.Include(item => item.Vendor)
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (contract is null)
        {
            return ContractOutcome.NotFound;
        }

        // A covered CI names the contract, and the foreign key refuses the delete; the guard turns
        // that into a 409 telling the operator to release the CIs first.
        if (await dbContext.Cis.AnyAsync(ci => ci.ContractId == id, cancellationToken))
        {
            return ContractOutcome.InUse;
        }

        var before = Map(contract, 0, ContractExpiryCalculator.Today());
        dbContext.Contracts.Remove(contract);
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditService.WriteAsync(actor, "Deleted", "Contract", id.ToString(), before, null, cancellationToken);
        return ContractOutcome.Success;
    }

    public async Task<CiResult> SetCoverageAsync(
        Guid ciId,
        SetCiCoverageRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        var ci = await dbContext.Cis
            .Include(item => item.CustomFieldValues).ThenInclude(value => value.Field)
            .Include(item => item.Contract).ThenInclude(contract => contract!.Vendor)
            .SingleOrDefaultAsync(item => item.Id == ciId, cancellationToken);
        if (ci is null)
        {
            return new(CiOutcome.NotFound);
        }

        // A disposed CI is a historical record; its coverage is what it was when it left the estate.
        if (ci.LifecycleState == CiLifecycleState.Disposed)
        {
            return new(CiOutcome.Disposed, Error: "A disposed CI's coverage can no longer be changed.");
        }

        Contract? contract = null;
        if (request.ContractId is { } contractId)
        {
            contract = await dbContext.Contracts.Include(item => item.Vendor)
                .SingleOrDefaultAsync(item => item.Id == contractId, cancellationToken);
            if (contract is null)
            {
                return new(CiOutcome.InvalidAttributes, Errors: new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    [nameof(request.ContractId)] = [$"Contract '{contractId}' does not exist."],
                });
            }
        }

        var before = CiService.Map(ci);
        ci.ContractId = contract?.Id;
        ci.Contract = contract;
        ci.PurchaseDate = request.PurchaseDate;
        ci.WarrantyExpiresAt = request.WarrantyExpiresAt;
        ci.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);

        var after = CiService.Map(ci);
        await auditService.WriteAsync(
            actor, "CoverageChanged", "Ci", ci.Id.ToString(), before, after, cancellationToken);
        return new(CiOutcome.Success, after);
    }

    private Task<bool> NumberTakenAsync(string number, Guid? excludingId, CancellationToken cancellationToken) =>
        dbContext.Contracts.AnyAsync(
            contract => contract.PoNumber.ToLower() == number.ToLower() && contract.Id != excludingId,
            cancellationToken);

    private async Task<(DirectoryUser? User, string? Error)> ResolveOwnerAsync(
        Guid? ownerUserId,
        CancellationToken cancellationToken)
    {
        if (ownerUserId is not { } id)
        {
            return (null, null);
        }

        var user = await directoryService.FindUserAsync(id, cancellationToken);
        return user is null ? (null, $"User '{id}' does not exist.") : (user, null);
    }

    /// <summary>
    /// The department, resolved against the platform's directory rather than taken on the caller's
    /// word — the same check CI assignment makes. The name is returned with it so the contract can
    /// snapshot both and stay readable after the department is renamed.
    /// </summary>
    private async Task<(DirectoryDepartment? Department, string? Error)> ResolveDepartmentAsync(
        Guid? departmentId,
        CancellationToken cancellationToken)
    {
        if (departmentId is not { } id)
        {
            return (null, null);
        }

        var department = await directoryService.FindDepartmentAsync(id, cancellationToken);
        return department is null ? (null, $"Department '{id}' does not exist.") : (department, null);
    }

    private static ContractResult Invalid(string field, string message) => new(
        ContractOutcome.Invalid,
        Errors: new Dictionary<string, string[]>(StringComparer.Ordinal) { [field] = [message] });

    private static string? Normalise(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// Canonicalises a purchase order number as <c>PO - 22-0419</c>.
    /// <para>
    /// The prefix is added here rather than in the browser so every caller gets the same value —
    /// and an existing one is stripped first, so a person who types "PO - 22-0419" out of habit does
    /// not end up with "PO - PO - 22-0419". Uniqueness is checked against the canonical form, which
    /// is the point: the same purchase order typed two ways has to collide.
    /// </para>
    /// </summary>
    public static string NormalisePoNumber(string value)
    {
        var trimmed = value.Trim();
        // "PO", "PO-", "PO :", "po - " — any spacing or separator somebody might type.
        var stripped = PoPrefix.Replace(trimmed, string.Empty).Trim();
        return stripped.Length == 0 ? trimmed : $"PO - {stripped}";
    }

    private static readonly Regex PoPrefix = new(
        @"^P\s*O\s*[-–—:]?\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    internal static ContractResponse Map(Contract contract, int coveredCiCount, DateOnly today) => new(
        contract.Id,
        contract.VendorId,
        contract.Vendor?.Name ?? string.Empty,
        contract.PoNumber,
        contract.Name,
        contract.Type,
        contract.StartDate,
        contract.EndDate,
        contract.AutoRenews,
        contract.Cost,
        contract.Currency,
        contract.OwnerUserId,
        contract.OwnerName,
        contract.OwnerEmail,
        contract.DepartmentId,
        contract.DepartmentName,
        contract.ContractNumber,
        contract.Notes,
        contract.IsActive,
        ContractExpiryCalculator.Status(contract.EndDate, today),
        ContractExpiryCalculator.DaysRemaining(contract.EndDate, today),
        coveredCiCount,
        contract.CreatedAt,
        contract.UpdatedAt);
}
