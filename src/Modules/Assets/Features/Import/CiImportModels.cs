using Modules.Assets.Data;

namespace Modules.Assets.Features.Import;

/// <summary>
/// A column the wizard can map a spreadsheet header onto. Attribute and custom-field keys carry a
/// prefix so a user-defined field can never collide with the CI's own columns.
/// </summary>
public sealed record CiImportTargetField(string Key, string Label, bool IsRequired, CiImportTargetKind Kind);

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
    public const string AttributePrefix = "attributes.";
    public const string CustomFieldPrefix = "customFields.";
}

/// <summary>
/// What the operator chose in the wizard: one CI type for the whole file, plus which header feeds
/// which target column. Mixed-type files are not supported — the attribute list depends on the type.
/// </summary>
public sealed record CiImportMapping(CiType Type, IReadOnlyDictionary<string, string> Columns);

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
    IReadOnlyList<string> Errors);

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
