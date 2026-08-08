using System.Security.Claims;

using Microsoft.EntityFrameworkCore;
using Modules.Assets.Data;
using Platform.Auditing;

namespace Modules.Assets.Features.Contracts;

public sealed class VendorService(AssetsDbContext dbContext, IAuditService auditService) : IVendorService
{
    internal const int MaximumPageSize = 200;

    public async Task<VendorPageResponse> ListAsync(VendorListRequest request, CancellationToken cancellationToken)
    {
        var page = Math.Max(request.Page, 1);
        var pageSize = Math.Clamp(request.PageSize, 1, MaximumPageSize);
        var query = dbContext.Vendors.AsQueryable();

        if (request.IsActive is not null)
        {
            query = query.Where(vendor => vendor.IsActive == request.IsActive);
        }

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var term = $"%{request.Search.Trim()}%";
            query = query.Where(vendor =>
                EF.Functions.ILike(vendor.Name, term)
                || (vendor.ContactName != null && EF.Functions.ILike(vendor.ContactName, term))
                || (vendor.ContactEmail != null && EF.Functions.ILike(vendor.ContactEmail, term)));
        }

        var total = await query.CountAsync(cancellationToken);
        var vendors = await query
            .OrderBy(vendor => vendor.Name).ThenBy(vendor => vendor.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(vendor => new { Vendor = vendor, ContractCount = vendor.Contracts.Count })
            .ToListAsync(cancellationToken);
        return new([.. vendors.Select(row => Map(row.Vendor, row.ContractCount))], total, page, pageSize);
    }

    public async Task<VendorResponse?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var row = await dbContext.Vendors
            .Where(vendor => vendor.Id == id)
            .Select(vendor => new { Vendor = vendor, ContractCount = vendor.Contracts.Count })
            .SingleOrDefaultAsync(cancellationToken);
        return row is null ? null : Map(row.Vendor, row.ContractCount);
    }

    public async Task<VendorResult> CreateAsync(
        CreateVendorRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        var name = request.Name.Trim();
        if (await NameTakenAsync(name, null, cancellationToken))
        {
            return new(ContractOutcome.Duplicate, Error: $"A vendor named '{name}' already exists.");
        }

        var now = DateTimeOffset.UtcNow;
        var vendor = new Vendor
        {
            Id = Guid.CreateVersion7(),
            Name = name,
            ContactName = Normalise(request.ContactName),
            ContactEmail = Normalise(request.ContactEmail),
            ContactPhone = Normalise(request.ContactPhone),
            Website = Normalise(request.Website),
            Notes = Normalise(request.Notes),
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        dbContext.Vendors.Add(vendor);
        await dbContext.SaveChangesAsync(cancellationToken);

        var response = Map(vendor, 0);
        await auditService.WriteAsync(
            actor, "Created", "Vendor", vendor.Id.ToString(), null, response, cancellationToken);
        return new(ContractOutcome.Success, response);
    }

    public async Task<VendorResult> UpdateAsync(
        Guid id,
        UpdateVendorRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        var vendor = await dbContext.Vendors.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (vendor is null)
        {
            return new(ContractOutcome.NotFound);
        }

        var name = request.Name.Trim();
        if (await NameTakenAsync(name, id, cancellationToken))
        {
            return new(ContractOutcome.Duplicate, Error: $"A vendor named '{name}' already exists.");
        }

        var contractCount = await dbContext.Contracts.CountAsync(
            contract => contract.VendorId == id, cancellationToken);
        var before = Map(vendor, contractCount);
        vendor.Name = name;
        vendor.ContactName = Normalise(request.ContactName);
        vendor.ContactEmail = Normalise(request.ContactEmail);
        vendor.ContactPhone = Normalise(request.ContactPhone);
        vendor.Website = Normalise(request.Website);
        vendor.Notes = Normalise(request.Notes);
        vendor.IsActive = request.IsActive;
        vendor.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);

        var after = Map(vendor, contractCount);
        await auditService.WriteAsync(actor, "Updated", "Vendor", id.ToString(), before, after, cancellationToken);
        return new(ContractOutcome.Success, after);
    }

    public async Task<ContractOutcome> DeleteAsync(Guid id, ClaimsPrincipal actor, CancellationToken cancellationToken)
    {
        var vendor = await dbContext.Vendors.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (vendor is null)
        {
            return ContractOutcome.NotFound;
        }

        // The foreign key refuses this anyway; catching it here turns a database error into a 409
        // that says what is in the way, exactly as the CI delete guard does.
        var contractCount = await dbContext.Contracts.CountAsync(
            contract => contract.VendorId == id, cancellationToken);
        if (contractCount > 0)
        {
            return ContractOutcome.InUse;
        }

        var before = Map(vendor, 0);
        dbContext.Vendors.Remove(vendor);
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditService.WriteAsync(actor, "Deleted", "Vendor", id.ToString(), before, null, cancellationToken);
        return ContractOutcome.Success;
    }

    private Task<bool> NameTakenAsync(string name, Guid? excludingId, CancellationToken cancellationToken) =>
        dbContext.Vendors.AnyAsync(
            vendor => vendor.Name.ToLower() == name.ToLower() && vendor.Id != excludingId,
            cancellationToken);

    private static string? Normalise(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    internal static VendorResponse Map(Vendor vendor, int contractCount) => new(
        vendor.Id,
        vendor.Name,
        vendor.ContactName,
        vendor.ContactEmail,
        vendor.ContactPhone,
        vendor.Website,
        vendor.Notes,
        vendor.IsActive,
        contractCount,
        vendor.CreatedAt,
        vendor.UpdatedAt);
}
