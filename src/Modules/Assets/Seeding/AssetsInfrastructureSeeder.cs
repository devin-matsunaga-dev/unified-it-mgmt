using Microsoft.EntityFrameworkCore;

using Modules.Assets.Data;
using Modules.Assets.Features.Cis;

using Platform.Directory;

namespace Modules.Assets.Seeding;

public sealed record AssetsInfrastructureSeedResult(
    int VendorsAdded,
    int ContractsAdded,
    int CustomFieldsAdded,
    int CustomFieldValuesAdded,
    int CisAdded,
    int RelationshipsAdded,
    int LifecycleEntriesAdded,
    int AssignmentEntriesAdded)
{
    /// <summary>
    /// The CIs a ticket is worth linking to, grouped by what a ticket would be about. Helpdesk owns
    /// ticket↔CI links and may not read this module, so the ids are handed to its seeder by the caller.
    /// </summary>
    public IReadOnlyList<Guid> HardwareCiIds { get; init; } = [];

    public IReadOnlyList<Guid> NetworkCiIds { get; init; } = [];

    public IReadOnlyList<Guid> ServiceCiIds { get; init; } = [];

    /// <summary>Every seeded CI's id, keyed by <see cref="CiSeed.Key"/>, whether it was added this run or already there.</summary>
    public IReadOnlyDictionary<string, Guid> CiIds { get; init; } =
        new Dictionary<string, Guid>(StringComparer.Ordinal);
}

/// <summary>
/// Writes <see cref="AssetsEstate"/> into the CMDB: 60 CIs with their lifecycle history, ownership,
/// coverage and dependency edges. The dev database is recreated on most AppHost restarts, so an estate
/// worth demonstrating has to be seeded rather than typed in.
/// <para>
/// It writes through the DbContext rather than <c>ICiService</c> on purpose. The service is one
/// transaction, one audit entry and one outbox message per CI — right for an operator's edit and for
/// the WP-2.5 importer's 5000-row ceiling, wrong for reference data nobody performed. The cost of
/// bypassing it is that <see cref="CiTypeSchema"/> is no longer applied automatically, so every CI is
/// bound through it here before the typed row is built, and the estate's conformance is asserted by a
/// unit test that needs no database.
/// </para>
/// <para>Re-running adds nothing: every id is derived from an item's position in the estate arrays.</para>
/// </summary>
public sealed class AssetsInfrastructureSeeder(AssetsDbContext dbContext, IDirectoryService directoryService)
{
    /// <summary>CIs in the seeded estate. Fixed so the dataset is reproducible.</summary>
    public const int CiCount = 60;

    private const string ActorId = "seeder";

    private const int VendorKind = 1;
    private const int ContractKind = 2;
    private const int CiKind = 3;
    private const int RelationshipKind = 4;
    private const int LifecycleKind = 5;
    private const int AssignmentKind = 6;
    private const int CustomFieldKind = 7;
    private const int CustomFieldValueKind = 8;

    public async Task<AssetsInfrastructureSeedResult> SeedAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var today = DateOnly.FromDateTime(now.UtcDateTime);

        var users = (await directoryService.ListUsersAsync(cancellationToken))
            .ToDictionary(user => user.Username, StringComparer.OrdinalIgnoreCase);
        var departments = (await directoryService.ListDepartmentsAsync(cancellationToken))
            .ToDictionary(department => department.Code, StringComparer.OrdinalIgnoreCase);
        var sites = (await directoryService.ListSitesAsync(cancellationToken))
            .ToDictionary(site => site.Code, StringComparer.OrdinalIgnoreCase);
        RequireDirectory(users, departments, sites);

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        var vendorsAdded = await SeedVendorsAsync(now, cancellationToken);
        var (contractsAdded, contractIds) = await SeedContractsAsync(users, today, now, cancellationToken);
        var (customFieldsAdded, customFieldIds) = await SeedCustomFieldsAsync(now, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);

        var counts = new SeedCounters();
        var ciIds = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var candidateIds = Enumerable.Range(0, AssetsEstate.Cis.Count)
            .Select(index => DeterministicId(CiKind, index)).ToArray();
        var existingCiIds = await dbContext.Cis.Where(ci => candidateIds.Contains(ci.Id))
            .Select(ci => ci.Id).ToHashSetAsync(cancellationToken);

        for (var index = 0; index < AssetsEstate.Cis.Count; index++)
        {
            var seed = AssetsEstate.Cis[index];
            var id = candidateIds[index];
            ciIds[seed.Key] = id;
            if (existingCiIds.Contains(id))
            {
                continue;
            }

            var owner = Resolve(seed.OwnerUsername, users);
            var previousOwner = Resolve(seed.PreviousOwnerUsername, users);
            var site = ResolveSite(seed, owner ?? previousOwner, sites);
            var department = ResolveDepartment(seed, owner ?? previousOwner, departments);
            var createdAt = now - TimeSpan.FromDays(seed.AgeDays);
            var history = BuildLifecycleHistory(seed.State, createdAt, seed.AgeDays);
            var deployedAt = history.LastOrDefault(entry => entry.ToState == CiLifecycleState.Deployed).OccurredAt;
            var assignedAt = deployedAt == default ? createdAt : deployedAt;

            var ci = Materialise(seed, id);
            ci.Name = seed.Name;
            ci.Description = seed.Description;
            ci.AssetTag = seed.AssetTag;
            ci.SerialNumber = seed.SerialNumber;
            ci.LifecycleState = seed.State;
            // Disposal is what takes a CI off the books; WP-2.2 clears IsActive with it.
            ci.IsActive = seed.State != CiLifecycleState.Disposed;
            ci.OwnerUserId = owner?.Id;
            ci.OwnerName = owner?.DisplayName;
            ci.DepartmentId = department?.Id;
            ci.DepartmentName = department?.Name;
            ci.SiteId = site?.Id;
            ci.SiteName = site?.Name;
            ci.AssignedAt = owner is null ? null : assignedAt;
            ci.PurchaseDate = seed.PurchasedDaysAgo is { } purchased ? today.AddDays(-purchased) : null;
            ci.WarrantyExpiresAt = seed.WarrantyInDays is { } warranty ? today.AddDays(warranty) : null;
            ci.ContractId = seed.ContractKey is { } contractKey ? contractIds[contractKey] : null;
            ci.CreatedAt = createdAt;
            ci.UpdatedAt = history.Count == 0 ? createdAt : history[^1].OccurredAt;
            dbContext.Cis.Add(ci);
            counts.Cis++;

            for (var step = 0; step < history.Count; step++)
            {
                var (fromState, toState, occurredAt) = history[step];
                dbContext.CiLifecycleHistory.Add(new CiLifecycleHistory
                {
                    Id = DeterministicId(LifecycleKind, index, step + 1),
                    CiId = id,
                    FromState = fromState,
                    ToState = toState,
                    Note = LifecycleNote(toState),
                    ActorId = ActorId,
                    OccurredAt = occurredAt,
                });
                counts.LifecycleEntries++;
            }

            foreach (var entry in BuildAssignmentLog(index, id, owner, previousOwner, site, department, assignedAt, now))
            {
                dbContext.CiAssignments.Add(entry);
                counts.AssignmentEntries++;
            }

            var valueIndex = 0;
            foreach (var (key, value) in seed.CustomFieldValues)
            {
                // A value can only exist for a field defined on this CI's own type.
                if (!customFieldIds.TryGetValue((seed.Type, key), out var fieldId))
                {
                    continue;
                }

                dbContext.CiCustomFieldValues.Add(new CiCustomFieldValue
                {
                    Id = DeterministicId(CustomFieldValueKind, index, ++valueIndex),
                    CiId = id,
                    FieldId = fieldId,
                    Value = value,
                    UpdatedAt = createdAt,
                });
                counts.CustomFieldValues++;
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        counts.Relationships = await SeedRelationshipsAsync(ciIds, now, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new AssetsInfrastructureSeedResult(
            vendorsAdded,
            contractsAdded,
            customFieldsAdded,
            counts.CustomFieldValues,
            counts.Cis,
            counts.Relationships,
            counts.LifecycleEntries,
            counts.AssignmentEntries)
        {
            HardwareCiIds = LinkTargets(ciIds, CiType.Hardware),
            NetworkCiIds = LinkTargets(ciIds, CiType.NetworkDevice),
            ServiceCiIds = LinkTargets(ciIds, CiType.Logical),
            CiIds = ciIds,
        };
    }

    private async Task<int> SeedVendorsAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var added = 0;
        for (var index = 0; index < AssetsEstate.Vendors.Count; index++)
        {
            var seed = AssetsEstate.Vendors[index];
            var id = DeterministicId(VendorKind, index);
            if (await dbContext.Vendors.AnyAsync(vendor => vendor.Id == id, cancellationToken))
            {
                continue;
            }

            dbContext.Vendors.Add(new Vendor
            {
                Id = id,
                Name = seed.Name,
                ContactName = seed.ContactName,
                ContactEmail = seed.ContactEmail,
                ContactPhone = seed.ContactPhone,
                Website = seed.Website,
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now,
            });
            added++;
        }

        return added;
    }

    private async Task<(int Added, Dictionary<string, Guid> Ids)> SeedContractsAsync(
        IReadOnlyDictionary<string, DirectoryUser> users,
        DateOnly today,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var added = 0;
        var ids = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var vendorIds = AssetsEstate.Vendors
            .Select((vendor, index) => (vendor.Key, Id: DeterministicId(VendorKind, index)))
            .ToDictionary(entry => entry.Key, entry => entry.Id, StringComparer.Ordinal);

        for (var index = 0; index < AssetsEstate.Contracts.Count; index++)
        {
            var seed = AssetsEstate.Contracts[index];
            var id = DeterministicId(ContractKind, index);
            ids[seed.Key] = id;
            if (await dbContext.Contracts.AnyAsync(contract => contract.Id == id, cancellationToken))
            {
                continue;
            }

            var owner = Resolve(seed.OwnerUsername, users);
            dbContext.Contracts.Add(new Contract
            {
                Id = id,
                VendorId = vendorIds[seed.VendorKey],
                PoNumber = seed.Number,
                Name = seed.Name,
                Type = seed.Type,
                StartDate = today.AddDays(-seed.StartDaysAgo),
                EndDate = today.AddDays(seed.EndInDays),
                AutoRenews = seed.AutoRenews,
                Cost = seed.Cost,
                Currency = "GBP",
                OwnerUserId = owner?.Id,
                OwnerName = owner?.DisplayName,
                OwnerEmail = owner?.Email,
                Notes = seed.Notes,
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now,
            });
            added++;
        }

        return (added, ids);
    }

    private async Task<(int Added, Dictionary<(CiType, string), Guid> Ids)> SeedCustomFieldsAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var added = 0;
        var ids = new Dictionary<(CiType, string), Guid>();
        for (var index = 0; index < AssetsEstate.CustomFields.Count; index++)
        {
            var seed = AssetsEstate.CustomFields[index];
            var id = DeterministicId(CustomFieldKind, index);
            ids[(seed.CiType, seed.Key)] = id;
            if (await dbContext.CiCustomFields.AnyAsync(field => field.Id == id, cancellationToken))
            {
                continue;
            }

            dbContext.CiCustomFields.Add(new CiCustomField
            {
                Id = id,
                CiType = seed.CiType,
                Key = seed.Key,
                Label = seed.Label,
                Type = seed.Type,
                IsRequired = false,
                Options = [.. seed.Options],
                SortOrder = seed.SortOrder,
                CreatedAt = now,
            });
            added++;
        }

        return (added, ids);
    }

    private async Task<int> SeedRelationshipsAsync(
        IReadOnlyDictionary<string, Guid> ciIds,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var candidateIds = Enumerable.Range(0, AssetsEstate.Relationships.Count)
            .Select(index => DeterministicId(RelationshipKind, index)).ToArray();
        var existing = await dbContext.CiRelationships.Where(relationship => candidateIds.Contains(relationship.Id))
            .Select(relationship => relationship.Id).ToHashSetAsync(cancellationToken);

        var added = 0;
        for (var index = 0; index < AssetsEstate.Relationships.Count; index++)
        {
            var seed = AssetsEstate.Relationships[index];
            if (existing.Contains(candidateIds[index]))
            {
                continue;
            }

            dbContext.CiRelationships.Add(new CiRelationship
            {
                Id = candidateIds[index],
                SourceCiId = ciIds[seed.SourceKey],
                TargetCiId = ciIds[seed.TargetKey],
                Type = seed.Type,
                Description = seed.Description,
                CreatedBy = ActorId,
                CreatedAt = now,
            });
            added++;
        }

        return added;
    }

    /// <summary>
    /// Builds the CI as its concrete type, binding the seeded attributes through
    /// <see cref="CiTypeSchema"/> first so a typo in the estate fails loudly here rather than writing a
    /// half-populated row that TPH's nullable columns would happily accept.
    /// </summary>
    private static ConfigurationItem Materialise(CiSeed seed, Guid id)
    {
        var bound = CiTypeSchema.Bind(seed.Type, seed.Attributes.ToDictionary(entry => entry.Key, entry => (string?)entry.Value));
        if (bound.Errors.Count > 0)
        {
            var detail = string.Join("; ", bound.Errors.Select(error => $"{error.Key}: {string.Join(" ", error.Value)}"));
            throw new InvalidOperationException($"Seeded CI '{seed.Key}' does not satisfy the {seed.Type} schema — {detail}");
        }

        var values = bound.Values;
        return seed.Type switch
        {
            CiType.Hardware => new HardwareCi
            {
                Id = id,
                Manufacturer = values["manufacturer"],
                Model = values["model"],
            },
            CiType.Server => new ServerCi
            {
                Id = id,
                Hostname = values["hostname"],
                OperatingSystem = values["operatingSystem"],
                CpuCores = int.Parse(values["cpuCores"]),
                RamGb = int.Parse(values["ramGb"]),
            },
            CiType.NetworkDevice => new NetworkDeviceCi
            {
                Id = id,
                ManagementIp = values["managementIp"],
                Vendor = values["vendor"],
                PortCount = int.Parse(values["portCount"]),
            },
            CiType.Software => new SoftwareCi
            {
                Id = id,
                Vendor = values["vendor"],
                Version = values["version"],
            },
            CiType.Virtual => new VirtualCi
            {
                Id = id,
                Hostname = values["hostname"],
                Hypervisor = values["hypervisor"],
                VcpuCores = int.Parse(values["vcpuCores"]),
                RamGb = int.Parse(values["ramGb"]),
            },
            CiType.Logical => new LogicalCi
            {
                Id = id,
                Purpose = values["purpose"],
                ServiceTier = values.GetValueOrDefault("serviceTier", string.Empty),
            },
            _ => throw new InvalidOperationException($"Unknown CI type '{seed.Type}'."),
        };
    }

    /// <summary>
    /// The transitions a CI in this state must have been through. Every pair is an edge the WP-2.2
    /// graph permits, so the seeded history is one an agent could actually have produced.
    /// </summary>
    private static IReadOnlyList<(CiLifecycleState FromState, CiLifecycleState ToState, DateTimeOffset OccurredAt)>
        BuildLifecycleHistory(CiLifecycleState state, DateTimeOffset createdAt, int ageDays)
    {
        // Every CI starts life Ordered (WP-2.2 allows a new CI only in Ordered or InStock).
        CiLifecycleState[] chain = state switch
        {
            CiLifecycleState.Ordered => [CiLifecycleState.Ordered],
            CiLifecycleState.InStock => [CiLifecycleState.Ordered, CiLifecycleState.InStock],
            CiLifecycleState.Deployed =>
                [CiLifecycleState.Ordered, CiLifecycleState.InStock, CiLifecycleState.Deployed],
            CiLifecycleState.InRepair =>
                [CiLifecycleState.Ordered, CiLifecycleState.InStock, CiLifecycleState.Deployed, CiLifecycleState.InRepair],
            CiLifecycleState.Retired =>
                [CiLifecycleState.Ordered, CiLifecycleState.InStock, CiLifecycleState.Deployed, CiLifecycleState.Retired],
            CiLifecycleState.Disposed =>
            [
                CiLifecycleState.Ordered, CiLifecycleState.InStock, CiLifecycleState.Deployed,
                CiLifecycleState.Retired, CiLifecycleState.Disposed,
            ],
            _ => throw new InvalidOperationException($"Unknown lifecycle state '{state}'."),
        };

        // Spread the moves over the first half of the CI's life, so the last one is comfortably in the
        // past and the record does not look like it changed the moment the seeder ran.
        var window = TimeSpan.FromDays(ageDays * 0.5);
        var steps = chain.Length - 1;
        return
        [
            .. Enumerable.Range(1, steps).Select(step =>
                (chain[step - 1], chain[step], createdAt + (window * step / (steps + 1d))))
        ];
    }

    private static string? LifecycleNote(CiLifecycleState toState) => toState switch
    {
        CiLifecycleState.InStock => "Received into stock.",
        CiLifecycleState.Deployed => "Deployed into service.",
        CiLifecycleState.InRepair => "Sent to the supplier for repair.",
        CiLifecycleState.Retired => "Withdrawn from service.",
        CiLifecycleState.Disposed => "Disposed of and removed from the estate.",
        _ => null,
    };

    /// <summary>
    /// The check-in/out log. A held asset shows the check-out that put it with its holder; an asset
    /// that was retired shows the check-out and the hand-back that followed; unheld infrastructure
    /// shows the relocation that placed it at its site.
    /// </summary>
    private static IEnumerable<CiAssignmentEntry> BuildAssignmentLog(
        int index,
        Guid ciId,
        DirectoryUser? owner,
        DirectoryUser? previousOwner,
        DirectorySite? site,
        DirectoryDepartment? department,
        DateTimeOffset assignedAt,
        DateTimeOffset now)
    {
        var holder = owner ?? previousOwner;
        if (holder is null)
        {
            if (site is null)
            {
                yield break;
            }

            yield return Entry(1, CiAssignmentAction.Relocate, null, null, null, null, assignedAt,
                "Placed at its site.");
            yield break;
        }

        yield return Entry(1, CiAssignmentAction.CheckOut, null, null, holder.Id, holder.DisplayName, assignedAt,
            "Checked out to its holder.");

        if (owner is null)
        {
            // WP-2.2 checks a CI back in automatically when it is retired or disposed.
            yield return Entry(2, CiAssignmentAction.CheckIn, holder.Id, holder.DisplayName, null, null,
                now - TimeSpan.FromDays(20), "Checked in when the asset was withdrawn from service.");
        }

        CiAssignmentEntry Entry(
            int child,
            CiAssignmentAction action,
            Guid? fromOwnerId,
            string? fromOwnerName,
            Guid? toOwnerId,
            string? toOwnerName,
            DateTimeOffset occurredAt,
            string note) => new()
            {
                Id = DeterministicId(AssignmentKind, index, child),
                CiId = ciId,
                Action = action,
                FromOwnerUserId = fromOwnerId,
                FromOwnerName = fromOwnerName,
                ToOwnerUserId = toOwnerId,
                ToOwnerName = toOwnerName,
                DepartmentId = department?.Id,
                DepartmentName = department?.Name,
                SiteId = site?.Id,
                SiteName = site?.Name,
                Note = note,
                ActorId = ActorId,
                OccurredAt = occurredAt,
            };
    }

    private static IReadOnlyList<Guid> LinkTargets(IReadOnlyDictionary<string, Guid> ciIds, CiType type) =>
    [
        // Only assets that are actually in service: a ticket about something still on order, already
        // retired or disposed of is not a link anyone would have made.
        .. AssetsEstate.Cis
            .Where(seed => seed.Type == type
                && seed.State is CiLifecycleState.Deployed or CiLifecycleState.InRepair)
            .Select(seed => ciIds[seed.Key])
    ];

    /// <summary>
    /// Fails before anything is written if the platform directory does not hold the people, departments
    /// and sites the estate names. Checking up front rather than per row means a seeder run against a
    /// database whose platform demo data is missing stops with one readable sentence instead of writing
    /// half an estate — the same crash-fast rule the services follow for missing configuration.
    /// </summary>
    private static void RequireDirectory(
        IReadOnlyDictionary<string, DirectoryUser> users,
        IReadOnlyDictionary<string, DirectoryDepartment> departments,
        IReadOnlyDictionary<string, DirectorySite> sites)
    {
        var missingUsers = AssetsEstate.Contracts.Select(contract => contract.OwnerUsername)
            .Concat(AssetsEstate.Cis.SelectMany(ci => new[] { ci.OwnerUsername, ci.PreviousOwnerUsername }))
            .OfType<string>()
            .Where(username => !users.ContainsKey(username));
        var missingDepartments = AssetsEstate.Cis.Select(ci => ci.DepartmentCode)
            .OfType<string>()
            .Where(code => !departments.ContainsKey(code));
        var missingSites = AssetsEstate.Cis.Select(ci => ci.SiteCode)
            .OfType<string>()
            .Where(code => !sites.ContainsKey(code));

        var missing = missingUsers.Concat(missingDepartments).Concat(missingSites)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidOperationException(
                $"The seeded estate references {string.Join(", ", missing)}, which the platform directory does not hold. Run the platform demo seeder first.");
        }
    }

    private static DirectoryUser? Resolve(string? username, IReadOnlyDictionary<string, DirectoryUser> users)
    {
        if (username is null)
        {
            return null;
        }

        return users.TryGetValue(username, out var user)
            ? user
            : throw new InvalidOperationException(
                $"The seeded estate references '{username}', who is not in the platform directory. Run the platform demo seeder first.");
    }

    private static DirectorySite? ResolveSite(
        CiSeed seed,
        DirectoryUser? holder,
        IReadOnlyDictionary<string, DirectorySite> sites)
    {
        if (seed.SiteCode is { } code)
        {
            return sites.TryGetValue(code, out var site)
                ? site
                : throw new InvalidOperationException($"The seeded estate references an unknown site '{code}'.");
        }

        // A held asset follows its holder, exactly as the assignment drawer prefills it.
        return holder is null ? null : new DirectorySite(holder.SiteId, string.Empty, holder.SiteName);
    }

    private static DirectoryDepartment? ResolveDepartment(
        CiSeed seed,
        DirectoryUser? holder,
        IReadOnlyDictionary<string, DirectoryDepartment> departments)
    {
        if (seed.DepartmentCode is { } code)
        {
            return departments.TryGetValue(code, out var department)
                ? department
                : throw new InvalidOperationException($"The seeded estate references an unknown department '{code}'.");
        }

        return holder is null ? null : new DirectoryDepartment(holder.DepartmentId, string.Empty, holder.DepartmentName);
    }

    private static Guid DeterministicId(int kind, int index, int child = 0) =>
        Guid.Parse($"01980002-{kind:0000}-7000-8000-{index:0000}{child:00000000}");

    private sealed class SeedCounters
    {
        public int Cis { get; set; }
        public int Relationships { get; set; }
        public int LifecycleEntries { get; set; }
        public int AssignmentEntries { get; set; }
        public int CustomFieldValues { get; set; }
    }
}
