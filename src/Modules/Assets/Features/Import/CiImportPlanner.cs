using Modules.Assets.Data;
using Modules.Assets.Features.Cis;

namespace Modules.Assets.Features.Import;

/// <summary>The mapped values one file row states. Blank cells are omitted — see <see cref="CiImportPlanner"/>.</summary>
public sealed record CiImportRowValues(
    int LineNumber,
    string? Name,
    string? AssetTag,
    string? SerialNumber,
    string? Description,
    IReadOnlyDictionary<string, string?> Attributes,
    IReadOnlyDictionary<string, string?> CustomFields);

/// <summary>
/// The mapping half of the import: which columns a CI type offers, which header probably feeds which,
/// whether a submitted mapping is usable, and what one row actually says.
///
/// A blank cell is "no statement", not "clear this value" — spreadsheet exports are full of blanks and
/// an import must never wipe a field the operator did not mean to touch. So blanks are dropped here,
/// which makes them a missing required value on a create and a left-alone value on an update.
///
/// Pure logic — no database access.
/// </summary>
public static class CiImportPlanner
{
    /// <summary>Header spellings an export is likely to use that neither the key nor the label matches.</summary>
    private static readonly IReadOnlyDictionary<string, string> Aliases =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["serial"] = CiImportTargets.SerialNumber,
            ["serialno"] = CiImportTargets.SerialNumber,
            ["servicetag"] = CiImportTargets.SerialNumber,
            ["tag"] = CiImportTargets.AssetTag,
            ["asset"] = CiImportTargets.AssetTag,
            ["notes"] = CiImportTargets.Description,
            ["hostname"] = CiImportTargets.Name,
        };

    public static IReadOnlyList<CiImportTargetField> TargetsFor(
        CiType type,
        IReadOnlyList<CiCustomField> customFields) =>
    [
        new(CiImportTargets.Name, "Name", true, CiImportTargetKind.Core),
        new(CiImportTargets.AssetTag, "Asset tag", false, CiImportTargetKind.Core),
        new(CiImportTargets.SerialNumber, "Serial number", false, CiImportTargetKind.Core),
        new(CiImportTargets.Description, "Description", false, CiImportTargetKind.Core),
        .. CiTypeSchema.For(type).Select(attribute => new CiImportTargetField(
            CiImportTargets.AttributePrefix + attribute.Key,
            attribute.Label,
            attribute.IsRequired,
            CiImportTargetKind.Attribute)),
        .. customFields.Where(field => field.CiType == type).Select(field => new CiImportTargetField(
            CiImportTargets.CustomFieldPrefix + field.Key,
            field.Label,
            field.IsRequired,
            CiImportTargetKind.CustomField)),
    ];

    /// <summary>
    /// A first guess at the mapping so the operator confirms rather than fills in a whole form. Matches
    /// on the target key, its label, or a known alias, ignoring case, spaces and punctuation.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Suggest(
        IReadOnlyList<CiImportTargetField> targets,
        IReadOnlyList<string> headers)
    {
        var suggestion = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var header in headers)
        {
            var normalised = Normalise(header);
            if (normalised.Length == 0)
            {
                continue;
            }

            var target = targets.FirstOrDefault(candidate =>
                    Normalise(Unprefixed(candidate.Key)) == normalised || Normalise(candidate.Label) == normalised)
                ?? (Aliases.TryGetValue(normalised, out var aliased)
                    ? targets.FirstOrDefault(candidate => candidate.Key == aliased)
                    : null);

            // First header wins: two columns feeding one target would make the row ambiguous.
            if (target is not null && !suggestion.ContainsKey(target.Key))
            {
                suggestion[target.Key] = header;
            }
        }

        return suggestion;
    }

    /// <summary>
    /// Field errors for a mapping the wizard submitted, keyed the way the form expects them back.
    /// </summary>
    public static IReadOnlyDictionary<string, string[]> ValidateMapping(
        CiImportMapping mapping,
        IReadOnlyList<CiImportTargetField> targets,
        IReadOnlyList<string> headers)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var (targetKey, header) in mapping.Columns)
        {
            if (targets.All(target => target.Key != targetKey))
            {
                errors[ErrorKey(targetKey)] = [$"'{targetKey}' is not a column of a {mapping.Type} CI."];
                continue;
            }

            if (!headers.Contains(header, StringComparer.Ordinal))
            {
                errors[ErrorKey(targetKey)] = [$"The file has no column named '{header}'."];
            }
        }

        var duplicate = mapping.Columns
            .GroupBy(entry => entry.Value, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            errors[ErrorKey(duplicate.First().Key)] =
                [$"Column '{duplicate.Key}' is mapped to more than one field."];
        }

        if (!mapping.Columns.ContainsKey(CiImportTargets.Name))
        {
            errors[ErrorKey(CiImportTargets.Name)] = ["Map a column to Name; every imported CI needs one."];
        }

        // Without an asset tag or a serial there is no key to match on, so a second run of the same file
        // would create a second copy of every row. Refusing the mapping is the only honest answer.
        if (!mapping.Columns.ContainsKey(CiImportTargets.AssetTag)
            && !mapping.Columns.ContainsKey(CiImportTargets.SerialNumber))
        {
            errors[ErrorKey(CiImportTargets.AssetTag)] =
                ["Map a column to Asset tag or Serial number so rows can be matched to existing CIs."];
        }

        return errors;
    }

    public static CiImportRowValues Extract(
        CiImportMapping mapping,
        IReadOnlyList<string> headers,
        CiImportRow row)
    {
        var attributes = new Dictionary<string, string?>(StringComparer.Ordinal);
        var customFields = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (targetKey, header) in mapping.Columns)
        {
            var value = Cell(headers, row, header);
            if (value is null)
            {
                continue;
            }

            if (targetKey.StartsWith(CiImportTargets.AttributePrefix, StringComparison.Ordinal))
            {
                attributes[targetKey[CiImportTargets.AttributePrefix.Length..]] = value;
            }
            else if (targetKey.StartsWith(CiImportTargets.CustomFieldPrefix, StringComparison.Ordinal))
            {
                customFields[targetKey[CiImportTargets.CustomFieldPrefix.Length..]] = value;
            }
        }

        return new(
            row.LineNumber,
            Core(mapping, headers, row, CiImportTargets.Name),
            Core(mapping, headers, row, CiImportTargets.AssetTag),
            Core(mapping, headers, row, CiImportTargets.SerialNumber),
            Core(mapping, headers, row, CiImportTargets.Description),
            attributes,
            customFields);
    }

    private static string? Core(
        CiImportMapping mapping,
        IReadOnlyList<string> headers,
        CiImportRow row,
        string targetKey) =>
        mapping.Columns.TryGetValue(targetKey, out var header) ? Cell(headers, row, header) : null;

    private static string? Cell(IReadOnlyList<string> headers, CiImportRow row, string header)
    {
        var index = -1;
        for (var candidate = 0; candidate < headers.Count; candidate++)
        {
            if (string.Equals(headers[candidate], header, StringComparison.Ordinal))
            {
                index = candidate;
                break;
            }
        }

        if (index < 0 || index >= row.Cells.Count)
        {
            return null;
        }

        var value = row.Cells[index].Trim();
        return value.Length == 0 ? null : value;
    }

    private static string Unprefixed(string targetKey) =>
        targetKey.StartsWith(CiImportTargets.AttributePrefix, StringComparison.Ordinal)
            ? targetKey[CiImportTargets.AttributePrefix.Length..]
            : targetKey.StartsWith(CiImportTargets.CustomFieldPrefix, StringComparison.Ordinal)
                ? targetKey[CiImportTargets.CustomFieldPrefix.Length..]
                : targetKey;

    private static string Normalise(string value) =>
        new([.. value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant)]);

    private static string ErrorKey(string targetKey) => $"mapping.{targetKey}";
}
