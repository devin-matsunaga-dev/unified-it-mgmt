using Modules.Assets.Data;

namespace Modules.Assets.Features.Import;

/// <summary>
/// A column the wizard can map a spreadsheet header onto. Attribute and custom-field keys carry a
/// prefix so a user-defined field can never collide with the CI's own columns.
///
/// <paramref name="Types"/> is populated only for a mixed-type import, where one target can belong to
/// several types and be required by some of them: a Hardware row and a Server row read different halves
/// of the same mapping form. It is null for a single-type import, whose <paramref name="IsRequired"/>
/// says everything there is to say.
/// </summary>
public sealed record CiImportTargetField(
    string Key,
    string Label,
    bool IsRequired,
    CiImportTargetKind Kind,
    IReadOnlyList<CiImportTargetType>? Types = null);

/// <summary>One CI type that declares a target, and whether that type demands a value for it.</summary>
public sealed record CiImportTargetType(CiType Type, bool IsRequired);

public enum CiImportTargetKind
{
    Core = 1,
    Attribute = 2,
    CustomField = 3,
}

public static class CiImportTargets
{
    public const string Name = "name";
    public const string AssetTag = "assetTag";
    public const string SerialNumber = "serialNumber";
    public const string Description = "description";

    /// <summary>Offered only by a mixed-type import: the column stating what each row is.</summary>
    public const string Type = "type";
    public const string AttributePrefix = "attributes.";
    public const string CustomFieldPrefix = "customFields.";
}

/// <summary>
/// What the operator chose in the wizard: which header feeds which target column, and either one CI
/// type for the whole file or — when <paramref name="Type"/> is null — a mixed file whose rows each
/// state or imply their own type (see <see cref="CiImportTypeResolver"/>).
///
/// <paramref name="AcceptInferredTypes"/> is the operator confirming the guesses the dry run showed
/// them. A commit that would create CIs of a guessed type without it is refused, because a TPH type is
/// permanent: the wrong guess is only undone by deleting the CI.
/// </summary>
public sealed record CiImportMapping(
    CiType? Type,
    IReadOnlyDictionary<string, string> Columns,
    bool AcceptInferredTypes = false);

/// <summary>Where a row's CI type came from, so the dry run can mark the ones that were guessed.</summary>
public enum CiImportTypeSource
{
    /// <summary>The operator chose one type for the whole file.</summary>
    Fixed = 1,

    /// <summary>The row's own type column stated it.</summary>
    Column = 2,

    /// <summary>Guessed from the attribute columns the row carries.</summary>
    Inferred = 3,
}

/// <summary>What one row of a mixed-type file turned out to be, or why it could not be decided.</summary>
public sealed record CiImportTypeResolution(CiType? Type, CiImportTypeSource Source, string? Error);

/// <summary>
/// The wire form of the type choice: a CI type name, or <see cref="Mixed"/> for a file whose rows carry
/// their own. A CI type is a TPH discriminator, so "mixed" cannot be a member of the enum itself.
/// </summary>
public static class CiImportTypeSelection
{
    public const string Mixed = "Mixed";

    public static bool TryParse(string? value, out CiType? type)
    {
        type = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        if (string.Equals(trimmed, Mixed, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (Enum.TryParse<CiType>(trimmed, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed))
        {
            type = parsed;
            return true;
        }

        return false;
    }
}

public enum CiImportAction
{
    Create = 1,
    Update = 2,
    Skip = 3,
    Error = 4,
}

public sealed record CiImportRowResult(
    int LineNumber,
    CiImportAction Action,
    string? Name,
    string? AssetTag,
    string? SerialNumber,
    Guid? MatchedCiId,
    IReadOnlyList<string> Errors,
    CiType? Type = null,
    CiImportTypeSource? TypeSource = null);

/// <summary>The dry run and the commit return the same shape, so the preview is literally what happened.</summary>
public sealed record CiImportReport(
    bool IsDryRun,
    int TotalRows,
    int Created,
    int Updated,
    int Skipped,
    int Failed,
    IReadOnlyList<CiImportRowResult> Rows);

public sealed record CiImportColumnsResponse(
    string FileName,
    IReadOnlyList<string> Headers,
    IReadOnlyList<IReadOnlyList<string>> SampleRows,
    int RowCount,
    IReadOnlyList<CiImportTargetField> Targets,
    IReadOnlyDictionary<string, string> SuggestedMapping);

public enum CiImportOutcome
{
    Success,
    InvalidFile,
    InvalidMapping,
}

public sealed record CiImportColumnsResult(
    CiImportOutcome Outcome,
    CiImportColumnsResponse? Columns = null,
    string? Error = null);

public sealed record CiImportResult(
    CiImportOutcome Outcome,
    CiImportReport? Report = null,
    string? Error = null,
    IReadOnlyDictionary<string, string[]>? Errors = null);
