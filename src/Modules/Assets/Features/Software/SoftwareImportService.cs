using System.Security.Claims;

using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

using Modules.Assets.Data;
using Modules.Assets.Features.Import;

using Platform.Auditing;

namespace Modules.Assets.Features.Software;

/// <summary>
/// The collection path: an inventory file from an agent, an RMM export or a WMI/SSH script, resolved
/// against the CMDB and normalised through the catalogue.
/// <para>
/// A row names a machine the way whoever exported it knows the machine — by asset tag, by serial or by
/// hostname — and is resolved serial-first then asset-tag then hostname, following WP-2.5's dedupe
/// order. A row naming a machine the CMDB does not hold is that row's error and not the file's: an
/// inventory export routinely covers a few devices nobody has recorded yet, and refusing the whole file
/// for them would make the feature unusable on the estate it is for.
/// </para>
/// </summary>
public sealed class SoftwareImportService(AssetsDbContext dbContext, IAuditService auditService)
    : ISoftwareImportService
{
    public Task<SoftwareImportResult> PreviewAsync(IFormFile file, CancellationToken cancellationToken) =>
        RunAsync(file, null, cancellationToken);

    public Task<SoftwareImportResult> CommitAsync(
        IFormFile file,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken) =>
        RunAsync(file, actor, cancellationToken);

    private async Task<SoftwareImportResult> RunAsync(
        IFormFile file,
        ClaimsPrincipal? actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);
        var isDryRun = actor is null;

        // The same reader the CI importer uses, with the same 5 MB and 5,000-row ceilings: an inventory
        // file is a spreadsheet like any other and there is no second CSV parser in this repository.
        var read = await CiImportFileReader.ReadAsync(file, cancellationToken);
        if (!read.IsSuccess)
        {
            return new(SoftwareImportOutcome.InvalidFile, Error: read.Error);
        }

        var plan = SoftwareImportPlanner.Plan(read.Table!);
        if (!plan.IsSuccess)
        {
            return new(SoftwareImportOutcome.InvalidFile, Error: plan.Error);
        }

        var fileName = Path.GetFileName(file.FileName);
        var source = string.IsNullOrWhiteSpace(fileName) ? "inventory import" : Truncate(fileName, 200)!;
        var machines = await ResolveMachinesAsync(plan.Rows, cancellationToken);
        var rules = await SoftwareCatalogService.ActiveRulesAsync(dbContext, cancellationToken);
        var productNames = await ProductNamesAsync(rules, cancellationToken);

        var ciIds = machines.Indexes.SelectMany(index => index.Values)
            .Select(machine => machine.CiId).OfType<Guid>().Distinct().ToArray();
        var existing = ciIds.Length == 0
            ? new Dictionary<(Guid, string), InstalledSoftware>()
            : await dbContext.InstalledSoftware
                .Where(install => ciIds.Contains(install.CiId))
                .ToDictionaryAsync(install => (install.CiId, install.IdentityKey), cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var results = new List<SoftwareImportRowResult>(plan.Rows.Count);
        var seen = new Dictionary<(Guid CiId, string IdentityKey), int>();
        var unrecognised = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var created = 0;
        var updated = 0;
        var normalised = 0;

        foreach (var row in plan.Rows)
        {
            var errors = new List<string>(row.Errors);
            var machine = errors.Count == 0 ? Resolve(row, machines) : null;
            if (errors.Count == 0)
            {
                if (machine is null)
                {
                    errors.Add($"No CI matches {Describe(row)}.");
                }
                else if (machine.Error is { } ambiguity)
                {
                    errors.Add(ambiguity);
                }
            }

            if (errors.Count > 0 || machine?.CiId is not { } ciId)
            {
                results.Add(new(row.LineNumber, SoftwareImportAction.Error, row.Machine,
                    NullIfEmpty(row.SoftwareName), row.Version, null, null, null, null, errors));
                continue;
            }

            var identityKey = SoftwareNormaliser.IdentityKeyFor(row.SoftwareName, row.Version);
            if (seen.TryGetValue((ciId, identityKey), out var earlierLine))
            {
                // The WP-2.5 rule, re-applied: a file that names one install twice is a mistake in
                // whatever produced it, and merging the two silently would hide it.
                results.Add(new(row.LineNumber, SoftwareImportAction.Error, row.Machine, row.SoftwareName,
                    row.Version, ciId, machine.CiName, null, null,
                    [$"Line {earlierLine} already lists this software for this machine."]));
                continue;
            }

            seen[(ciId, identityKey)] = row.LineNumber;
            var productId = SoftwareNormaliser.Match(row.SoftwareName, rules);
            if (productId is null)
            {
                unrecognised.Add(row.SoftwareName);
            }
            else
            {
                normalised++;
            }

            var isUpdate = existing.TryGetValue((ciId, identityKey), out var install);
            if (isUpdate)
            {
                updated++;
            }
            else
            {
                created++;
            }

            if (!isDryRun)
            {
                if (install is null)
                {
                    install = new InstalledSoftware
                    {
                        Id = Guid.CreateVersion7(),
                        CiId = ciId,
                        IdentityKey = identityKey,
                        FirstSeenAt = now,
                        SightingCount = 0,
                    };
                    dbContext.InstalledSoftware.Add(install);
                    existing[(ciId, identityKey)] = install;
                }

                install.RawName = Truncate(row.SoftwareName, 300)!;
                install.RawPublisher = Truncate(row.Publisher, 200);
                install.Version = Truncate(row.Version, 100);
                install.ProductId = productId;

                // A blank install date leaves the recorded one alone, following WP-2.5's rule that a
                // blank cell means "do not touch" rather than "clear it".
                install.InstalledOn = row.InstalledOn ?? install.InstalledOn;
                install.Source = source;
                install.LastSeenAt = now;
                install.SightingCount++;
            }

            results.Add(new(
                row.LineNumber,
                isUpdate ? SoftwareImportAction.Update : SoftwareImportAction.Create,
                row.Machine,
                row.SoftwareName,
                row.Version,
                ciId,
                machine.CiName,
                productId,
                productId is { } matched ? productNames.GetValueOrDefault(matched) : null,
                []));
        }

        var failed = results.Count(result => result.Action == SoftwareImportAction.Error);
        if (!isDryRun)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            await auditService.WriteAsync(
                actor!,
                "Imported",
                "InstalledSoftware",
                source,
                null,
                new { TotalRows = plan.Rows.Count, Created = created, Updated = updated, Failed = failed },
                cancellationToken);
        }

        return new(SoftwareImportOutcome.Success, new SoftwareImportReport(
            isDryRun,
            source,
            plan.Rows.Count,
            created,
            updated,
            failed,
            results.Select(result => result.CiId).OfType<Guid>().Distinct().Count(),
            normalised,
            unrecognised.Count,
            results,
            [.. unrecognised.OrderBy(name => name, StringComparer.OrdinalIgnoreCase)]));
    }

    /// <summary>
    /// Every machine the file names, in a handful of bounded queries rather than one per row: a
    /// 5,000-row file covering 50 machines costs the same as a 50-row one.
    /// </summary>
    private async Task<MachineIndexes> ResolveMachinesAsync(
        IReadOnlyList<SoftwareImportParsedRow> rows,
        CancellationToken cancellationToken)
    {
        var serials = Distinct(rows.Select(row => row.SerialNumber));
        var tags = Distinct(rows.Select(row => row.AssetTag));
        var names = Distinct(rows.Select(row => row.Hostname));

        var bySerial = new List<MachineRow>();
        if (serials.Length > 0)
        {
            bySerial.AddRange(await dbContext.Cis.AsNoTracking()
                .Where(ci => ci.SerialNumber != null && serials.Contains(ci.SerialNumber.ToLower()))
                .Select(ci => new MachineRow(ci.SerialNumber!.ToLower(), ci.Id, ci.Name))
                .ToListAsync(cancellationToken));
        }

        var byTag = new List<MachineRow>();
        if (tags.Length > 0)
        {
            byTag.AddRange(await dbContext.Cis.AsNoTracking()
                .Where(ci => ci.AssetTag != null && tags.Contains(ci.AssetTag.ToLower()))
                .Select(ci => new MachineRow(ci.AssetTag!.ToLower(), ci.Id, ci.Name))
                .ToListAsync(cancellationToken));
        }

        // A hostname is matched against the two CI types that have one and then against the CI's own
        // name, because an agent reports the machine's name and the CMDB may hold it either way.
        var byName = new List<MachineRow>();
        if (names.Length > 0)
        {
            byName.AddRange(await dbContext.Cis.AsNoTracking().OfType<ServerCi>()
                .Where(ci => names.Contains(ci.Hostname.ToLower()))
                .Select(ci => new MachineRow(ci.Hostname.ToLower(), ci.Id, ci.Name))
                .ToListAsync(cancellationToken));
            byName.AddRange(await dbContext.Cis.AsNoTracking().OfType<VirtualCi>()
                .Where(ci => names.Contains(ci.Hostname.ToLower()))
                .Select(ci => new MachineRow(ci.Hostname.ToLower(), ci.Id, ci.Name))
                .ToListAsync(cancellationToken));
            byName.AddRange(await dbContext.Cis.AsNoTracking()
                .Where(ci => names.Contains(ci.Name.ToLower()))
                .Select(ci => new MachineRow(ci.Name.ToLower(), ci.Id, ci.Name))
                .ToListAsync(cancellationToken));
        }

        return new(
            Collapse(bySerial, "serial number"),
            Collapse(byTag, "asset tag"),
            Collapse(byName, "hostname"));
    }

    private async Task<IReadOnlyDictionary<Guid, string>> ProductNamesAsync(
        IReadOnlyList<SoftwareRule> rules,
        CancellationToken cancellationToken)
    {
        var wanted = rules.Select(rule => rule.ProductId).Distinct().ToArray();
        return wanted.Length == 0
            ? new Dictionary<Guid, string>()
            : await dbContext.SoftwareProducts.AsNoTracking()
                .Where(product => wanted.Contains(product.Id))
                .ToDictionaryAsync(product => product.Id, product => product.Name, cancellationToken);
    }

    /// <summary>
    /// Serial first, then asset tag, then hostname: the WP-2.5 dedupe order with the least reliable
    /// identifier last. A column that names nothing falls through to the next rather than failing the
    /// row, because an export routinely carries an empty serial.
    /// </summary>
    private static ResolvedMachine? Resolve(SoftwareImportParsedRow row, MachineIndexes machines) =>
        Lookup(machines.BySerial, row.SerialNumber)
        ?? Lookup(machines.ByAssetTag, row.AssetTag)
        ?? Lookup(machines.ByName, row.Hostname);

    private static ResolvedMachine? Lookup(IReadOnlyDictionary<string, ResolvedMachine> index, string? value) =>
        value is not null && index.TryGetValue(value.ToLowerInvariant(), out var machine) ? machine : null;

    /// <summary>
    /// One entry per key. Two CIs answering to one hostname is a contradiction in the estate rather than
    /// a machine this import can pick between, so the entry carries the refusal instead of a winner —
    /// the same call WP-4.2 makes for an ambiguous discovery.
    /// </summary>
    private static IReadOnlyDictionary<string, ResolvedMachine> Collapse(
        IReadOnlyList<MachineRow> rows,
        string what) =>
        rows.GroupBy(row => row.Key, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.DistinctBy(row => row.Id).Count() == 1
                    ? new ResolvedMachine(group.First().Id, group.First().Name, null)
                    : new ResolvedMachine(
                        null,
                        null,
                        $"More than one CI has the {what} '{group.Key}'; the row cannot say which machine it means."),
                StringComparer.Ordinal);

    private static string[] Distinct(IEnumerable<string?> values) =>
        [.. values.OfType<string>().Select(value => value.ToLowerInvariant()).Distinct(StringComparer.Ordinal)];

    private static string Describe(SoftwareImportParsedRow row)
    {
        var parts = new List<string>(3);
        if (row.SerialNumber is { } serial)
        {
            parts.Add($"serial '{serial}'");
        }

        if (row.AssetTag is { } tag)
        {
            parts.Add($"asset tag '{tag}'");
        }

        if (row.Hostname is { } hostname)
        {
            parts.Add($"hostname '{hostname}'");
        }

        return string.Join(" or ", parts);
    }

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

    private static string? Truncate(string? value, int length) =>
        value is null || value.Length <= length ? value : value[..length];

    private sealed record MachineRow(string Key, Guid Id, string Name);

    private sealed record ResolvedMachine(Guid? CiId, string? CiName, string? Error);

    private sealed record MachineIndexes(
        IReadOnlyDictionary<string, ResolvedMachine> BySerial,
        IReadOnlyDictionary<string, ResolvedMachine> ByAssetTag,
        IReadOnlyDictionary<string, ResolvedMachine> ByName)
    {
        public IEnumerable<IReadOnlyDictionary<string, ResolvedMachine>> Indexes => [BySerial, ByAssetTag, ByName];
    }
}
