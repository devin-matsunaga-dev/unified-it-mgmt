using System.Globalization;
using System.Security.Claims;

using Contracts.Events;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Modules.Assets.Data;
using Modules.Assets.Features.Contracts;
using Platform.Auditing;
using Platform.Integration;

namespace Modules.Assets.Features.Cis;

public sealed class CiService(
    AssetsDbContext dbContext,
    IPublishEndpoint publishEndpoint,
    IAuditService auditService,
    ITicketLinkDirectory ticketLinks) : ICiService
{
    internal const int MaximumPageSize = 200;

    public async Task<CiPageResponse> ListAsync(CiListRequest request, CancellationToken cancellationToken)
    {
        var page = Math.Max(request.Page, 1);
        var pageSize = Math.Clamp(request.PageSize, 1, MaximumPageSize);
        var query = dbContext.Cis
            .Include(ci => ci.CustomFieldValues).ThenInclude(value => value.Field)
            .Include(ci => ci.Contract).ThenInclude(contract => contract!.Vendor)
            .AsQueryable();

        if (request.Type is not null)
        {
            // EF translates the TPH discriminator check from the CLR type, so filter by type rather
            // than by a column the base entity does not expose.
            query = request.Type switch
            {
                CiType.Hardware => query.OfType<HardwareCi>(),
                CiType.Server => query.OfType<ServerCi>(),
                CiType.NetworkDevice => query.OfType<NetworkDeviceCi>(),
                CiType.Software => query.OfType<SoftwareCi>(),
                CiType.Virtual => query.OfType<VirtualCi>(),
                CiType.Logical => query.OfType<LogicalCi>(),
                _ => throw new InvalidOperationException($"Unknown CI type '{request.Type}'."),
            };
        }

        foreach (var constraint in request.CustomFields ?? [])
        {
            // Captured per iteration: a closure over the loop variable would leave every predicate
            // pointing at the last constraint, and the list would silently narrow on one field.
            var fieldId = constraint.FieldId;
            var value = constraint.Value;
            query = query.Where(ci => ci.CustomFieldValues
                .Any(item => item.FieldId == fieldId && item.Value == value));
        }

        if (request.IsActive is not null)
        {
            query = query.Where(ci => ci.IsActive == request.IsActive);
        }

        if (request.LifecycleState is not null)
        {
            query = query.Where(ci => ci.LifecycleState == request.LifecycleState);
        }

        if (request.OwnerUserId is not null)
        {
            query = query.Where(ci => ci.OwnerUserId == request.OwnerUserId);
        }

        if (request.DepartmentId is not null)
        {
            query = query.Where(ci => ci.DepartmentId == request.DepartmentId);
        }

        if (request.SiteId is not null)
        {
            query = query.Where(ci => ci.SiteId == request.SiteId);
        }

        if (request.ContractId is not null)
        {
            query = query.Where(ci => ci.ContractId == request.ContractId);
        }

        // The contract page's companion view: assets whose own warranty runs out inside a window,
        // whether or not a contract covers them.
        if (request.WarrantyExpiringWithinDays is { } withinDays)
        {
            var boundary = ContractExpiryCalculator.Today().AddDays(Math.Clamp(withinDays, 0, 3_650));
            query = query.Where(ci => ci.WarrantyExpiresAt != null && ci.WarrantyExpiresAt <= boundary);
        }

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var term = $"%{request.Search.Trim()}%";
            query = query.Where(ci =>
                EF.Functions.ILike(ci.Name, term)
                || (ci.AssetTag != null && EF.Functions.ILike(ci.AssetTag, term))
                || (ci.SerialNumber != null && EF.Functions.ILike(ci.SerialNumber, term)));
        }

        var total = await query.CountAsync(cancellationToken);
        var cis = await query
            .OrderBy(ci => ci.Name).ThenBy(ci => ci.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
        return new([.. cis.Select(Map)], total, page, pageSize);
    }

    public async Task<CiResponse?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var ci = await LoadAsync(id, cancellationToken);
        return ci is null ? null : Map(ci);
    }

    public async Task<CiResult> CreateAsync(
        CreateCiRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var attributes = CiTypeSchema.Bind(request.Type, request.Attributes);
        if (attributes.Errors.Count > 0)
        {
            return new(CiOutcome.InvalidAttributes, Errors: attributes.Errors);
        }

        // Registration is only ever "on order" or "in the store room"; Deployed and everything past
        // it has to be reached through a guarded transition so the history is never skipped.
        if (request.LifecycleState is not (CiLifecycleState.Ordered or CiLifecycleState.InStock))
        {
            return new(CiOutcome.InvalidAttributes, Errors: new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                [nameof(request.LifecycleState)] =
                    ["A new CI starts as Ordered or InStock; use a lifecycle transition to move it on."],
            });
        }

        var definitions = await FieldsForAsync(request.Type, cancellationToken);
        var bound = CiCustomFieldValueBinder.Bind(definitions, request.CustomFields);
        if (bound.Errors.Count > 0)
        {
            return new(CiOutcome.InvalidCustomFields, Errors: bound.Errors);
        }

        var assetTag = Normalise(request.AssetTag);
        var serialNumber = Normalise(request.SerialNumber);
        if (await DuplicateIdentifierAsync(assetTag, serialNumber, null, cancellationToken) is { } duplicate)
        {
            return new(CiOutcome.DuplicateIdentifier, Error: duplicate);
        }

        var ci = NewCi(request.Type);
        ci.Id = Guid.CreateVersion7();
        ci.Name = request.Name.Trim();
        ci.AssetTag = assetTag;
        ci.SerialNumber = serialNumber;
        ci.Description = Normalise(request.Description);
        ci.IsActive = true;
        ci.LifecycleState = request.LifecycleState;
        ci.CreatedAt = now;
        ci.UpdatedAt = now;
        ApplyAttributes(ci, attributes.Values);
        foreach (var (fieldId, value) in bound.Values)
        {
            ci.CustomFieldValues.Add(new CiCustomFieldValue
            {
                Id = Guid.CreateVersion7(), CiId = ci.Id, FieldId = fieldId,
                Field = definitions.Single(field => field.Id == fieldId), Value = value, UpdatedAt = now,
            });
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        dbContext.Cis.Add(ci);
        await dbContext.SaveChangesAsync(cancellationToken);
        await publishEndpoint.Publish(
            new CiCreated(Guid.CreateVersion7(), now, ci.Id, ci.Type.ToString(), ci.Name, ci.AssetTag),
            cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var response = Map(ci);
        await auditService.WriteAsync(actor, "Created", "Ci", ci.Id.ToString(), null, response, cancellationToken);
        return new(CiOutcome.Success, response);
    }

    public async Task<CiResult> UpdateAsync(
        Guid id,
        UpdateCiRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        var ci = await LoadAsync(id, cancellationToken);
        if (ci is null)
        {
            return new(CiOutcome.NotFound);
        }

        // A disposed CI is a historical record. Editing one would rewrite what the asset was when it
        // left the estate, so it is frozen rather than merely inactive.
        if (ci.LifecycleState == CiLifecycleState.Disposed)
        {
            return new(CiOutcome.Disposed, Error: "A disposed CI can no longer be edited.");
        }

        var now = DateTimeOffset.UtcNow;
        var attributes = CiTypeSchema.Bind(ci.Type, request.Attributes);
        if (attributes.Errors.Count > 0)
        {
            return new(CiOutcome.InvalidAttributes, Errors: attributes.Errors);
        }

        var definitions = await FieldsForAsync(ci.Type, cancellationToken);
        var bound = CiCustomFieldValueBinder.Bind(definitions, request.CustomFields);
        if (bound.Errors.Count > 0)
        {
            return new(CiOutcome.InvalidCustomFields, Errors: bound.Errors);
        }

        var assetTag = Normalise(request.AssetTag);
        var serialNumber = Normalise(request.SerialNumber);
        if (await DuplicateIdentifierAsync(assetTag, serialNumber, id, cancellationToken) is { } duplicate)
        {
            return new(CiOutcome.DuplicateIdentifier, Error: duplicate);
        }

        var before = Map(ci);
        ci.Name = request.Name.Trim();
        ci.AssetTag = assetTag;
        ci.SerialNumber = serialNumber;
        ci.Description = Normalise(request.Description);
        ci.IsActive = request.IsActive;
        ci.UpdatedAt = now;
        ApplyAttributes(ci, attributes.Values);

        // Values whose field is no longer submitted are removed, so clearing an optional field in the
        // form actually clears it in storage rather than leaving the previous value behind.
        foreach (var existing in ci.CustomFieldValues.ToList())
        {
            if (bound.Values.TryGetValue(existing.FieldId, out var updated))
            {
                existing.Value = updated;
                existing.UpdatedAt = now;
            }
            else
            {
                dbContext.CiCustomFieldValues.Remove(existing);
                ci.CustomFieldValues.Remove(existing);
            }
        }

        foreach (var (fieldId, value) in bound.Values)
        {
            if (ci.CustomFieldValues.Any(item => item.FieldId == fieldId))
            {
                continue;
            }

            ci.CustomFieldValues.Add(new CiCustomFieldValue
            {
                Id = Guid.CreateVersion7(), CiId = ci.Id, FieldId = fieldId,
                Field = definitions.Single(field => field.Id == fieldId), Value = value, UpdatedAt = now,
            });
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await publishEndpoint.Publish(
            new CiUpdated(Guid.CreateVersion7(), now, ci.Id, ci.Type.ToString(), ci.Name, ci.IsActive),
            cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var after = Map(ci);
        await auditService.WriteAsync(actor, "Updated", "Ci", ci.Id.ToString(), before, after, cancellationToken);
        return new(CiOutcome.Success, after);
    }

    public async Task<CiOutcome> DeleteAsync(Guid id, ClaimsPrincipal actor, CancellationToken cancellationToken)
    {
        var ci = await LoadAsync(id, cancellationToken);
        if (ci is null)
        {
            return CiOutcome.NotFound;
        }

        // Relationships name this CI on one end or the other, and the foreign keys refuse the delete
        // anyway; catching it here turns a database error into a 409 that says what is in the way.
        if (await dbContext.CiRelationships.AnyAsync(
                relationship => relationship.SourceCiId == id || relationship.TargetCiId == id,
                cancellationToken))
        {
            return CiOutcome.InUse;
        }

        // Ticket links live in the helpdesk schema, so no foreign key can catch this one: without the
        // port call the delete would leave every linked ticket pointing at a CI that no longer exists.
        if (await ticketLinks.CountLinksForCiAsync(id, cancellationToken) > 0)
        {
            return CiOutcome.InUse;
        }

        // A CI listed on a change is half of an agreement somebody made about it (WP-5.8). Its foreign
        // key is Restrict and would refuse the delete anyway; catching it here says what is in the way.
        if (await dbContext.ChangeRequestCis.AnyAsync(scope => scope.CiId == id, cancellationToken))
        {
            return CiOutcome.InUse;
        }

        var now = DateTimeOffset.UtcNow;
        var before = Map(ci);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        dbContext.Cis.Remove(ci);
        await dbContext.SaveChangesAsync(cancellationToken);
        await publishEndpoint.Publish(
            new CiDeleted(Guid.CreateVersion7(), now, id, before.Type.ToString()), cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        await auditService.WriteAsync(actor, "Deleted", "Ci", id.ToString(), before, null, cancellationToken);
        return CiOutcome.Success;
    }

    public async Task<IReadOnlyList<CiTypeSchemaResponse>> GetSchemasAsync(CancellationToken cancellationToken)
    {
        var fields = await dbContext.CiCustomFields
            .OrderBy(field => field.SortOrder).ThenBy(field => field.Label)
            .ToListAsync(cancellationToken);
        return
        [
            .. CiTypeSchema.All.Select(entry => new CiTypeSchemaResponse(
                entry.Key,
                entry.Value,
                [.. fields.Where(field => field.CiType == entry.Key).Select(Map)]))
        ];
    }

    public async Task<CiCustomFieldResult> AddFieldAsync(
        CreateCiCustomFieldRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        var key = request.Key.Trim();
        if (await dbContext.CiCustomFields.AnyAsync(
                field => field.CiType == request.CiType && field.Key.ToLower() == key.ToLower(), cancellationToken))
        {
            return new(CiOutcome.DuplicateIdentifier, Error: $"A field with key '{key}' already exists on {request.CiType}.");
        }

        if (CiTypeSchema.For(request.CiType).Any(
                attribute => string.Equals(attribute.Key, key, StringComparison.OrdinalIgnoreCase)))
        {
            return new(
                CiOutcome.DuplicateIdentifier,
                Error: $"'{key}' is a built-in attribute of {request.CiType} and cannot be redefined.");
        }

        var field = new CiCustomField
        {
            Id = Guid.CreateVersion7(),
            CiType = request.CiType,
            Key = key,
            Label = request.Label.Trim(),
            Type = request.Type,
            IsRequired = request.IsRequired,
            Options = request.Type == CiCustomFieldType.Select
                ? [.. (request.Options ?? []).Select(option => option.Trim())]
                : [],
            SortOrder = request.SortOrder,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        dbContext.CiCustomFields.Add(field);
        await dbContext.SaveChangesAsync(cancellationToken);

        var response = Map(field);
        await auditService.WriteAsync(
            actor, "Created", "CiCustomField", field.Id.ToString(), null, response, cancellationToken);
        return new(CiOutcome.Success, response);
    }

    public async Task<CiCustomFieldResult> UpdateFieldAsync(
        Guid fieldId,
        UpdateCiCustomFieldRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        var field = await dbContext.CiCustomFields
            .SingleOrDefaultAsync(item => item.Id == fieldId, cancellationToken);
        if (field is null)
        {
            return new(CiOutcome.NotFound);
        }

        var options = field.Type == CiCustomFieldType.Select
            ? (request.Options ?? []).Select(option => option.Trim()).Where(option => option.Length > 0).ToList()
            : [];

        // Adding an option strands nothing — values are stored against the field's id, not its text.
        // Removing one does: CiCustomFieldValueBinder validates every Select value on every CI write,
        // and the CI form submits the whole set, so a CI still holding a removed option would fail
        // validation on its next edit for a field nobody touched. Refuse instead, and say how many.
        if (field.Type == CiCustomFieldType.Select)
        {
            var kept = options.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var removed = field.Options.Where(option => !kept.Contains(option)).ToArray();
            if (removed.Length > 0)
            {
                var counts = await ValueCountsAsync(fieldId, cancellationToken);
                var stranded = counts
                    .Where(count => removed.Contains(count.Value, StringComparer.OrdinalIgnoreCase))
                    .ToArray();
                if (stranded.Length > 0)
                {
                    var detail = string.Join(
                        "; ",
                        stranded.Select(count => $"{count.CiCount} on '{count.Value}'"));
                    return new(
                        CiOutcome.InUse,
                        Error: $"Configuration items still hold options you are removing ({detail}). Change those first.");
                }
            }
        }

        var before = Map(field);
        field.Label = request.Label.Trim();
        field.IsRequired = request.IsRequired;
        field.SortOrder = request.SortOrder;
        field.Options = options;
        await dbContext.SaveChangesAsync(cancellationToken);

        var after = Map(field);
        await auditService.WriteAsync(
            actor, "Updated", "CiCustomField", field.Id.ToString(), before, after, cancellationToken);
        return new(CiOutcome.Success, after);
    }

    public async Task<IReadOnlyList<CiCustomFieldValueCount>> GetFieldValueCountsAsync(
        Guid fieldId,
        CancellationToken cancellationToken) =>
        await ValueCountsAsync(fieldId, cancellationToken);

    /// <summary>
    /// Grouped into an anonymous type and mapped afterwards: EF cannot translate a GroupBy that
    /// projects straight into a record constructor, and the alternative is the whole values table.
    /// </summary>
    private async Task<IReadOnlyList<CiCustomFieldValueCount>> ValueCountsAsync(
        Guid fieldId,
        CancellationToken cancellationToken)
    {
        var rows = await dbContext.CiCustomFieldValues
            .Where(value => value.FieldId == fieldId)
            .GroupBy(value => value.Value)
            .Select(group => new { Value = group.Key, CiCount = group.Count() })
            .ToListAsync(cancellationToken);

        return [.. rows
            .Select(row => new CiCustomFieldValueCount(row.Value, row.CiCount))
            .OrderByDescending(count => count.CiCount)
            .ThenBy(count => count.Value, StringComparer.OrdinalIgnoreCase)];
    }

    public async Task<CiOutcome> DeleteFieldAsync(
        Guid fieldId,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        var field = await dbContext.CiCustomFields
            .SingleOrDefaultAsync(item => item.Id == fieldId, cancellationToken);
        if (field is null)
        {
            return CiOutcome.NotFound;
        }

        if (await dbContext.CiCustomFieldValues.AnyAsync(value => value.FieldId == fieldId, cancellationToken))
        {
            return CiOutcome.InUse;
        }

        var before = Map(field);
        dbContext.CiCustomFields.Remove(field);
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditService.WriteAsync(
            actor, "Deleted", "CiCustomField", fieldId.ToString(), before, null, cancellationToken);
        return CiOutcome.Success;
    }

    private Task<ConfigurationItem?> LoadAsync(Guid id, CancellationToken cancellationToken) =>
        dbContext.Cis.Include(ci => ci.CustomFieldValues).ThenInclude(value => value.Field)
            .Include(ci => ci.Contract).ThenInclude(contract => contract!.Vendor)
            .SingleOrDefaultAsync(ci => ci.Id == id, cancellationToken);

    private async Task<IReadOnlyList<CiCustomField>> FieldsForAsync(CiType type, CancellationToken cancellationToken) =>
        await dbContext.CiCustomFields.Where(field => field.CiType == type)
            .OrderBy(field => field.SortOrder).ThenBy(field => field.Label)
            .ToListAsync(cancellationToken);

    private async Task<string?> DuplicateIdentifierAsync(
        string? assetTag,
        string? serialNumber,
        Guid? excludingId,
        CancellationToken cancellationToken)
    {
        if (assetTag is not null && await dbContext.Cis.AnyAsync(
                ci => ci.AssetTag != null && ci.AssetTag.ToLower() == assetTag.ToLower() && ci.Id != excludingId,
                cancellationToken))
        {
            return $"Asset tag '{assetTag}' is already used by another CI.";
        }

        if (serialNumber is not null && await dbContext.Cis.AnyAsync(
                ci => ci.SerialNumber != null && ci.SerialNumber.ToLower() == serialNumber.ToLower()
                    && ci.Id != excludingId,
                cancellationToken))
        {
            return $"Serial number '{serialNumber}' is already used by another CI.";
        }

        return null;
    }

    private static string? Normalise(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static ConfigurationItem NewCi(CiType type) => type switch
    {
        CiType.Hardware => new HardwareCi(),
        CiType.Server => new ServerCi(),
        CiType.NetworkDevice => new NetworkDeviceCi(),
        CiType.Software => new SoftwareCi(),
        CiType.Virtual => new VirtualCi(),
        CiType.Logical => new LogicalCi(),
        _ => throw new InvalidOperationException($"Unknown CI type '{type}'."),
    };

    private static void ApplyAttributes(ConfigurationItem ci, IReadOnlyDictionary<string, string> values)
    {
        switch (ci)
        {
            case HardwareCi hardware:
                hardware.Manufacturer = Text(values, "manufacturer");
                hardware.Model = Text(values, "model");
                break;
            case ServerCi server:
                server.Hostname = Text(values, "hostname");
                server.OperatingSystem = Text(values, "operatingSystem");
                server.CpuCores = Integer(values, "cpuCores");
                server.RamGb = Integer(values, "ramGb");
                break;
            case NetworkDeviceCi network:
                network.ManagementIp = Text(values, "managementIp");
                network.Vendor = Text(values, "vendor");
                network.PortCount = Integer(values, "portCount");
                // Optional, so an absent value clears the role rather than defaulting it to something.
                network.Role = values.TryGetValue("role", out var role) && !string.IsNullOrWhiteSpace(role)
                    ? role
                    : null;
                break;
            case SoftwareCi software:
                software.Vendor = Text(values, "vendor");
                software.Version = Text(values, "version");
                break;
            case VirtualCi virtualCi:
                virtualCi.Hostname = Text(values, "hostname");
                virtualCi.Hypervisor = Text(values, "hypervisor");
                virtualCi.VcpuCores = Integer(values, "vcpuCores");
                virtualCi.RamGb = Integer(values, "ramGb");
                break;
            case LogicalCi logical:
                logical.Purpose = Text(values, "purpose");
                logical.ServiceTier = Text(values, "serviceTier");
                break;
            default:
                throw new InvalidOperationException($"Unknown CI entity '{ci.GetType().Name}'.");
        }
    }

    private static IReadOnlyDictionary<string, string> ReadAttributes(ConfigurationItem ci) => ci switch
    {
        HardwareCi hardware => new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["manufacturer"] = hardware.Manufacturer,
            ["model"] = hardware.Model,
        },
        ServerCi server => new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["hostname"] = server.Hostname,
            ["operatingSystem"] = server.OperatingSystem,
            ["cpuCores"] = server.CpuCores.ToString(CultureInfo.InvariantCulture),
            ["ramGb"] = server.RamGb.ToString(CultureInfo.InvariantCulture),
        },
        // Role is omitted rather than reported as an empty string: it is the one optional attribute
        // in the schema, and "present but blank" is not the same claim as "not recorded".
        NetworkDeviceCi network => new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["managementIp"] = network.ManagementIp,
            ["vendor"] = network.Vendor,
            ["portCount"] = network.PortCount.ToString(CultureInfo.InvariantCulture),
        }.Concat(network.Role is null
                ? []
                : new[] { new KeyValuePair<string, string>("role", network.Role) })
            .ToDictionary(StringComparer.Ordinal),
        SoftwareCi software => new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["vendor"] = software.Vendor,
            ["version"] = software.Version,
        },
        VirtualCi virtualCi => new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["hostname"] = virtualCi.Hostname,
            ["hypervisor"] = virtualCi.Hypervisor,
            ["vcpuCores"] = virtualCi.VcpuCores.ToString(CultureInfo.InvariantCulture),
            ["ramGb"] = virtualCi.RamGb.ToString(CultureInfo.InvariantCulture),
        },
        LogicalCi logical => new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["purpose"] = logical.Purpose,
            ["serviceTier"] = logical.ServiceTier,
        },
        _ => throw new InvalidOperationException($"Unknown CI entity '{ci.GetType().Name}'."),
    };

    private static string Text(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) ? value : string.Empty;

    private static int Integer(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value)
            ? int.Parse(value, CultureInfo.InvariantCulture)
            : 0;

    internal static CiResponse Map(ConfigurationItem ci) => new(
        ci.Id,
        ci.Type,
        ci.Name,
        ci.AssetTag,
        ci.SerialNumber,
        ci.Description,
        ci.IsActive,
        ci.LifecycleState,
        new CiOwnership(
            ci.OwnerUserId,
            ci.OwnerName,
            ci.DepartmentId,
            ci.DepartmentName,
            ci.SiteId,
            ci.SiteName,
            ci.AssignedAt),
        MapCoverage(ci),
        ReadAttributes(ci),
        [.. ci.CustomFieldValues
            .OrderBy(value => value.Field.SortOrder).ThenBy(value => value.Field.Label)
            .Select(value => new CiCustomFieldValueResponse(
                value.FieldId, value.Field.Key, value.Field.Label, value.Field.Type, value.Value))],
        ci.CreatedAt,
        ci.UpdatedAt);

    /// <summary>
    /// Contract fields are read from the loaded relationship rather than snapshotted on the CI, so a
    /// renamed contract reaches every CI it covers at once — the same rule WP-2.4 gave ticket links.
    /// </summary>
    private static CiCoverage MapCoverage(ConfigurationItem ci)
    {
        var today = ContractExpiryCalculator.Today();
        return new(
            ci.ContractId,
            ci.Contract?.Name,
            ci.Contract?.PoNumber,
            ci.Contract?.Vendor?.Name,
            ci.Contract?.EndDate,
            ci.PurchaseDate,
            ci.WarrantyExpiresAt,
            ci.WarrantyExpiresAt is { } warranty ? ContractExpiryCalculator.Status(warranty, today) : null,
            ci.WarrantyExpiresAt is { } expiry ? ContractExpiryCalculator.DaysRemaining(expiry, today) : null);
    }

    internal static CiCustomFieldResponse Map(CiCustomField field) => new(
        field.Id,
        field.CiType,
        field.Key,
        field.Label,
        field.Type,
        field.IsRequired,
        field.Options,
        field.SortOrder);
}
