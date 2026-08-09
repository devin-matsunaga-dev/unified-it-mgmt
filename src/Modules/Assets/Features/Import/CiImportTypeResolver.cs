using Modules.Assets.Data;
using Modules.Assets.Features.Cis;

namespace Modules.Assets.Features.Import;

/// <summary>
/// Which CI type one row of a mixed-type file is. A mapped type column is authoritative and is never
/// second-guessed; where the file has no such column the type is inferred from the attribute columns
/// only one type declares. A row that states nothing usable is refused rather than guessed at, because
/// the TPH type is permanent — there is no path from Hardware to Server but delete and re-create.
///
/// Pure logic — no database access.
/// </summary>
public static class CiImportTypeResolver
{
    /// <summary>
    /// Attribute keys exactly one CI type declares, so a row carrying one names its own type. Derived
    /// from the schema rather than listed by hand: an attribute added to a second type stops being a
    /// discriminator on its own, which is the honest answer.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, CiType> Discriminators =
        CiTypeSchema.All
            .SelectMany(entry => entry.Value.Select(definition => (definition.Key, Type: entry.Key)))
            .GroupBy(entry => entry.Key, StringComparer.Ordinal)
            .Where(group => group.Select(entry => entry.Type).Distinct().Count() == 1)
            .ToDictionary(group => group.Key, group => group.First().Type, StringComparer.Ordinal);

    private static readonly string TypeNames = string.Join(", ", Enum.GetNames<CiType>());

    public static CiImportTypeResolution Resolve(CiImportMapping mapping, CiImportRowValues row)
    {
        if (mapping.Type is not null)
        {
            return new(mapping.Type, CiImportTypeSource.Fixed, null);
        }

        if (mapping.Columns.ContainsKey(CiImportTargets.Type))
        {
            if (row.TypeCell is null)
            {
                return new(
                    null,
                    CiImportTypeSource.Column,
                    "The CI type cell is blank; every row of a mixed-type file must name its type.");
            }

            return TryParseCell(row.TypeCell, out var stated)
                ? new(stated, CiImportTypeSource.Column, null)
                : new(
                    null,
                    CiImportTypeSource.Column,
                    $"'{row.TypeCell}' is not a CI type. Use one of {TypeNames}.");
        }

        var candidates = row.Attributes.Keys
            .Where(Discriminators.ContainsKey)
            .Select(key => Discriminators[key])
            .Distinct()
            .Order()
            .ToList();
        return candidates.Count switch
        {
            1 => new(candidates[0], CiImportTypeSource.Inferred, null),
            0 => new(
                null,
                CiImportTypeSource.Inferred,
                "The CI type could not be guessed: the row fills no column that belongs to one type only. "
                + "Map a CI type column."),
            _ => new(
                null,
                CiImportTypeSource.Inferred,
                $"The CI type is ambiguous: the row fills columns of {string.Join(" and ", candidates)}. "
                + "Map a CI type column."),
        };
    }

    /// <summary>Reads a type cell as an operator would write it — "Network device" is NetworkDevice.</summary>
    public static bool TryParseCell(string cell, out CiType type)
    {
        var normalised = Normalise(cell);
        foreach (var candidate in Enum.GetValues<CiType>())
        {
            if (Normalise(candidate.ToString()) == normalised)
            {
                type = candidate;
                return true;
            }
        }

        type = default;
        return false;
    }

    private static string Normalise(string value) =>
        new([.. value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant)]);
}
