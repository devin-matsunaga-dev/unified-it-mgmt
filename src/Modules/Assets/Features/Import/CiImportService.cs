using System.Security.Claims;

using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Modules.Assets.Data;
using Modules.Assets.Features.Cis;
using Platform.Auditing;

namespace Modules.Assets.Features.Import;

/// <summary>
/// The import itself. Rows are matched to existing CIs by serial number first and asset tag second —
/// the two identifiers WP-2.1 put filtered unique indexes on — so running the same file twice updates
/// or skips rather than duplicating.
///
/// Every write goes through <see cref="ICiService"/> rather than the DbContext, so an imported CI is
/// validated, audited and published exactly like one typed into the form, and nothing here has to
/// repeat the CiTypeSchema binding rules.
/// </summary>
public sealed class CiImportService(
    AssetsDbContext dbContext,
    ICiService ciService,
    IAuditService auditService) : ICiImportService
{
    private const int SampleRowCount = 5;

    public async Task<CiImportColumnsResult> InspectAsync(
        IFormFile file,
        CiType? type,
        CancellationToken cancellationToken)
    {
        var read = await CiImportFileReader.ReadAsync(file, cancellationToken);
        if (read.Table is null)
        {
            return new(CiImportOutcome.InvalidFile, Error: read.Error);
        }

        var targets = CiImportPlanner.TargetsFor(type, await CustomFieldsAsync(type, cancellationToken));
        return new(
            CiImportOutcome.Success,
            new CiImportColumnsResponse(
                Path.GetFileName(file.FileName),
                read.Table.Headers,
                [.. read.Table.Rows.Take(SampleRowCount).Select(row => row.Cells)],
                read.Table.Rows.Count,
                targets,
                CiImportPlanner.Suggest(targets, read.Table.Headers)));
    }

    public async Task<CiImportResult> PreviewAsync(
        IFormFile file,
        CiImportMapping mapping,
        CancellationToken cancellationToken)
    {
        var planned = await PlanAsync(file, mapping, cancellationToken);
        return planned.Failure ?? new(CiImportOutcome.Success, PlannedReport(planned.Rows, isDryRun: true));
    }

    public async Task<CiImportResult> CommitAsync(
        IFormFile file,
        CiImportMapping mapping,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        var planned = await PlanAsync(file, mapping, cancellationToken);
        if (planned.Failure is not null)
        {
            return planned.Failure;
        }

        // A guessed type is only ever as good as the operator's reading of the dry run, and it cannot be
        // corrected afterwards — the CI has to be deleted and made again. So a commit that would write
        // one is refused until the wizard says the guesses were seen.
        if (!mapping.AcceptInferredTypes
            && planned.Rows.Any(row => row.Result.TypeSource == CiImportTypeSource.Inferred
                && row.Result.Action is CiImportAction.Create or CiImportAction.Update))
        {
            return new(
                CiImportOutcome.InvalidMapping,
                Errors: new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    [$"mapping.{CiImportTargets.Type}"] =
                    [
                        "This file has rows whose CI type was guessed. Review the guesses in the dry run "
                        + "before importing, or map a CI type column.",
                    ],
                });
        }

        var results = new List<CiImportRowResult>(planned.Rows.Count);
        foreach (var row in planned.Rows)
        {
            results.Add(await ApplyAsync(row, actor, cancellationToken));
        }

        var report = Report(results, isDryRun: false);
        await auditService.WriteAsync(
            actor,
            "Imported",
            "CiImport",
            Guid.CreateVersion7().ToString(),
            null,
            new
            {
                FileName = Path.GetFileName(file.FileName),
                Type = mapping.Type?.ToString() ?? CiImportTypeSelection.Mixed,
                report.TotalRows,
                report.Created,
                report.Updated,
                report.Skipped,
                report.Failed,
            },
            cancellationToken);
        return new(CiImportOutcome.Success, report);
    }

    private async Task<CiImportRowResult> ApplyAsync(
        PlannedRow row,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        if (row.Result.Action is CiImportAction.Error or CiImportAction.Skip)
        {
            return row.Result;
        }

        var result = row.Create is not null
            ? await ciService.CreateAsync(row.Create, actor, cancellationToken)
            : await ciService.UpdateAsync(row.Result.MatchedCiId!.Value, row.Update!, actor, cancellationToken);
        if (result.Outcome == CiOutcome.Success)
        {
            return row.Result;
        }

        // The plan validated every row against the same rules, so reaching here means the database moved
        // under the import (a concurrent write took the asset tag, say). Report the row, keep going.
        var errors = result.Errors?.SelectMany(entry => entry.Value).ToList() ?? [];
        if (result.Error is not null)
        {
            errors.Add(result.Error);
        }

        return row.Result with
        {
            Action = CiImportAction.Error,
            Errors = errors.Count > 0 ? errors : [$"The row could not be imported ({result.Outcome})."],
        };
    }

    private async Task<PlanResult> PlanAsync(
        IFormFile file,
        CiImportMapping mapping,
        CancellationToken cancellationToken)
    {
        var read = await CiImportFileReader.ReadAsync(file, cancellationToken);
        if (read.Table is null)
        {
            return new([], new(CiImportOutcome.InvalidFile, Error: read.Error));
        }

        var customFields = await CustomFieldsAsync(mapping.Type, cancellationToken);
        var targets = CiImportPlanner.TargetsFor(mapping.Type, customFields);
        var mappingErrors = CiImportPlanner.ValidateMapping(mapping, targets, read.Table.Headers);
        if (mappingErrors.Count > 0)
        {
            return new([], new(CiImportOutcome.InvalidMapping, Errors: mappingErrors));
        }

        var values = read.Table.Rows
            .Select(row => CiImportPlanner.Extract(mapping, read.Table.Headers, row))
            .ToList();
        var matches = await MatchesAsync(values, cancellationToken);

        var rows = new List<PlannedRow>(values.Count);
        var seenAssetTags = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var seenSerials = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in values)
        {
            rows.Add(Plan(row, mapping, customFields, matches, seenAssetTags, seenSerials));
        }

        return new(rows, null);
    }

    private static PlannedRow Plan(
        CiImportRowValues row,
        CiImportMapping mapping,
        IReadOnlyList<CiCustomField> customFields,
        IReadOnlyDictionary<string, ConfigurationItem> matches,
        Dictionary<string, int> seenAssetTags,
        Dictionary<string, int> seenSerials)
    {
        var errors = new List<string>();
        var resolution = CiImportTypeResolver.Resolve(mapping, row);
        if (resolution.Error is not null)
        {
            errors.Add(resolution.Error);
        }

        // A file that names the same asset twice would create it once and then collide with itself, so
        // the later row is refused rather than silently overwriting the earlier one.
        if (row.AssetTag is not null && !seenAssetTags.TryAdd(row.AssetTag, row.LineNumber))
        {
            errors.Add($"Asset tag '{row.AssetTag}' is already used by line {seenAssetTags[row.AssetTag]}.");
        }

        if (row.SerialNumber is not null && !seenSerials.TryAdd(row.SerialNumber, row.LineNumber))
        {
            errors.Add($"Serial number '{row.SerialNumber}' is already used by line {seenSerials[row.SerialNumber]}.");
        }

        var byAssetTag = Lookup(matches, CiImportTargets.AssetTag, row.AssetTag);
        var bySerial = Lookup(matches, CiImportTargets.SerialNumber, row.SerialNumber);
        if (byAssetTag is not null && bySerial is not null && byAssetTag.Id != bySerial.Id)
        {
            errors.Add(
                $"The asset tag matches '{byAssetTag.Name}' but the serial number matches '{bySerial.Name}'.");
        }

        var existing = bySerial ?? byAssetTag;
        if (existing is not null && resolution.Type is not null && existing.Type != resolution.Type)
        {
            errors.Add($"'{existing.Name}' is already registered as a {existing.Type} CI, not a {resolution.Type}.");
        }

        if (existing is not null && existing.LifecycleState == CiLifecycleState.Disposed)
        {
            errors.Add($"'{existing.Name}' is disposed and can no longer be edited.");
        }

        if (existing is null && row.Name is null)
        {
            errors.Add("Name is required to create a CI.");
        }

        if (errors.Count > 0)
        {
            return Failed(row, existing?.Id, errors, resolution);
        }

        // A mixed file is one sheet of everything, so most rows carry columns belonging to some other
        // type. Those are this row's blanks, not its errors — a single-type import keeps rejecting them,
        // because there the whole file was declared to be of one shape.
        var type = resolution.Type!.Value;
        var typeCustomFields = customFields.Where(field => field.CiType == type).ToList();
        var stated = mapping.Type is null ? OnlyDeclaredAttributes(type, row.Attributes) : row.Attributes;
        var statedCustom = mapping.Type is null
            ? OnlyDeclaredCustomFields(typeCustomFields, row.CustomFields)
            : row.CustomFields;

        var attributes = Merge(stated, existing);
        var bound = CiTypeSchema.Bind(type, attributes);
        var boundCustom = CiCustomFieldValueBinder.Bind(typeCustomFields, MergeCustom(statedCustom, existing));
        errors.AddRange(bound.Errors.SelectMany(entry => entry.Value));
        errors.AddRange(boundCustom.Errors.SelectMany(entry => entry.Value));
        if (errors.Count > 0)
        {
            return Failed(row, existing?.Id, errors, resolution);
        }

        if (existing is null)
        {
            return new(
                new CiImportRowResult(
                    row.LineNumber,
                    CiImportAction.Create,
                    row.Name,
                    row.AssetTag,
                    row.SerialNumber,
                    null,
                    [],
                    type,
                    resolution.Source),
                new CreateCiRequest(
                    type,
                    row.Name!,
                    row.AssetTag,
                    row.SerialNumber,
                    row.Description,
                    attributes,
                    statedCustom),
                null);
        }

        var name = row.Name ?? existing.Name;
        var assetTag = row.AssetTag ?? existing.AssetTag;
        var serialNumber = row.SerialNumber ?? existing.SerialNumber;
        var description = row.Description ?? existing.Description;
        var unchanged = name == existing.Name
            && assetTag == existing.AssetTag
            && serialNumber == existing.SerialNumber
            && description == existing.Description
            && SameAttributes(bound.Values, existing, type)
            && SameCustomFields(boundCustom.Values, existing);
        if (unchanged)
        {
            return new(
                new CiImportRowResult(
                    row.LineNumber,
                    CiImportAction.Skip,
                    name,
                    assetTag,
                    serialNumber,
                    existing.Id,
                    [],
                    type,
                    resolution.Source),
                null,
                null);
        }

        return new(
            new CiImportRowResult(
                row.LineNumber,
                CiImportAction.Update,
                name,
                assetTag,
                serialNumber,
                existing.Id,
                [],
                type,
                resolution.Source),
            null,
            new UpdateCiRequest(
                name,
                assetTag,
                serialNumber,
                description,
                existing.IsActive,
                attributes,
                MergeCustom(statedCustom, existing)));
    }

    /// <summary>Drops the columns some other type owns, so a mixed file's other halves read as blanks.</summary>
    private static IReadOnlyDictionary<string, string?> OnlyDeclaredAttributes(
        CiType type,
        IReadOnlyDictionary<string, string?> stated)
    {
        var declared = CiTypeSchema.For(type);
        return stated
            .Where(entry => declared.Any(definition =>
                string.Equals(definition.Key, entry.Key, StringComparison.OrdinalIgnoreCase)))
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
    }

    private static IReadOnlyDictionary<string, string?> OnlyDeclaredCustomFields(
        IReadOnlyList<CiCustomField> fields,
        IReadOnlyDictionary<string, string?> stated) =>
        stated
            .Where(entry => fields.Any(field =>
                string.Equals(field.Key, entry.Key, StringComparison.OrdinalIgnoreCase)))
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);

    /// <summary>
    /// Loads every CI the file could match in one query. Rows carrying neither identifier match nothing
    /// and are always creates.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, ConfigurationItem>> MatchesAsync(
        IReadOnlyList<CiImportRowValues> rows,
        CancellationToken cancellationToken)
    {
        var assetTags = rows.Where(row => row.AssetTag is not null)
            .Select(row => row.AssetTag!.ToLowerInvariant()).Distinct().ToList();
        var serials = rows.Where(row => row.SerialNumber is not null)
            .Select(row => row.SerialNumber!.ToLowerInvariant()).Distinct().ToList();
        if (assetTags.Count == 0 && serials.Count == 0)
        {
            return new Dictionary<string, ConfigurationItem>(StringComparer.Ordinal);
        }

        var candidates = await dbContext.Cis
            .Include(ci => ci.CustomFieldValues).ThenInclude(value => value.Field)
            .Where(ci =>
                (ci.AssetTag != null && assetTags.Contains(ci.AssetTag.ToLower()))
                || (ci.SerialNumber != null && serials.Contains(ci.SerialNumber.ToLower())))
            .ToListAsync(cancellationToken);

        var matches = new Dictionary<string, ConfigurationItem>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            if (candidate.AssetTag is not null)
            {
                matches[Key(CiImportTargets.AssetTag, candidate.AssetTag)] = candidate;
            }

            if (candidate.SerialNumber is not null)
            {
                matches[Key(CiImportTargets.SerialNumber, candidate.SerialNumber)] = candidate;
            }
        }

        return matches;
    }

    /// <summary>A mixed import needs every type's fields loaded; each row then reads only its own.</summary>
    private Task<List<CiCustomField>> CustomFieldsAsync(CiType? type, CancellationToken cancellationToken) =>
        dbContext.CiCustomFields.Where(field => type == null || field.CiType == type)
            .OrderBy(field => field.SortOrder).ThenBy(field => field.Label)
            .ToListAsync(cancellationToken);

    private static ConfigurationItem? Lookup(
        IReadOnlyDictionary<string, ConfigurationItem> matches,
        string kind,
        string? value) =>
        value is not null && matches.TryGetValue(Key(kind, value), out var match) ? match : null;

    private static string Key(string kind, string value) => $"{kind}:{value.ToLowerInvariant()}";

    /// <summary>A mapped blank leaves the existing value alone, so an update starts from what is stored.</summary>
    private static IReadOnlyDictionary<string, string?> Merge(
        IReadOnlyDictionary<string, string?> submitted,
        ConfigurationItem? existing)
    {
        if (existing is null)
        {
            return submitted;
        }

        var merged = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (key, value) in CiService.Map(existing).Attributes)
        {
            merged[key] = value;
        }

        foreach (var (key, value) in submitted)
        {
            merged[key] = value;
        }

        return merged;
    }

    private static IReadOnlyDictionary<string, string?> MergeCustom(
        IReadOnlyDictionary<string, string?> submitted,
        ConfigurationItem? existing)
    {
        if (existing is null)
        {
            return submitted;
        }

        var merged = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var value in existing.CustomFieldValues)
        {
            merged[value.Field.Key] = value.Value;
        }

        foreach (var (key, value) in submitted)
        {
            merged[key] = value;
        }

        return merged;
    }

    private static bool SameAttributes(
        IReadOnlyDictionary<string, string> bound,
        ConfigurationItem existing,
        CiType type)
    {
        var current = CiTypeSchema.Bind(type, Merge(new Dictionary<string, string?>(StringComparer.Ordinal), existing));
        return bound.Count == current.Values.Count
            && bound.All(entry =>
                current.Values.TryGetValue(entry.Key, out var value) && value == entry.Value);
    }

    private static bool SameCustomFields(
        IReadOnlyDictionary<Guid, string> bound,
        ConfigurationItem existing) =>
        bound.Count == existing.CustomFieldValues.Count
        && bound.All(entry => existing.CustomFieldValues
            .Any(value => value.FieldId == entry.Key && value.Value == entry.Value));

    private static PlannedRow Failed(
        CiImportRowValues row,
        Guid? matchedId,
        IReadOnlyList<string> errors,
        CiImportTypeResolution resolution) =>
        new(
            new CiImportRowResult(
                row.LineNumber,
                CiImportAction.Error,
                row.Name,
                row.AssetTag,
                row.SerialNumber,
                matchedId,
                errors,
                resolution.Type,
                resolution.Source),
            null,
            null);

    private static CiImportReport PlannedReport(IReadOnlyList<PlannedRow> rows, bool isDryRun) =>
        Report([.. rows.Select(row => row.Result)], isDryRun);

    private static CiImportReport Report(IReadOnlyList<CiImportRowResult> rows, bool isDryRun) => new(
        isDryRun,
        rows.Count,
        rows.Count(row => row.Action == CiImportAction.Create),
        rows.Count(row => row.Action == CiImportAction.Update),
        rows.Count(row => row.Action == CiImportAction.Skip),
        rows.Count(row => row.Action == CiImportAction.Error),
        rows);

    private sealed record PlannedRow(
        CiImportRowResult Result,
        CreateCiRequest? Create,
        UpdateCiRequest? Update);

    private sealed record PlanResult(IReadOnlyList<PlannedRow> Rows, CiImportResult? Failure);
}
