using Modules.Assets.Data;

namespace Modules.Assets.Features.Labels;

/// <summary>
/// The two stock label formats. Both are printed three-up or four-up on plain A4 for a batch, and on
/// a page cut to the label itself for a single asset, so either sheet stock or a dedicated label
/// printer works without a second document.
/// </summary>
public enum CiLabelSize
{
    /// <summary>63.5 × 33.9 mm — 3 × 7 on A4 (Avery L7159 and its equivalents).</summary>
    Standard,

    /// <summary>45.7 × 21.2 mm — 4 × 12 on A4 (Avery L7654), for laptops and small network gear.</summary>
    Small,
}

/// <summary>A batch print. The ids arrive in the order the operator selected them and stay in it.</summary>
public sealed record CiLabelSheetRequest(IReadOnlyList<Guid> CiIds, CiLabelSize Size = CiLabelSize.Standard);

/// <summary>One label's worth of facts, already trimmed to what fits on the stock.</summary>
public sealed record CiLabel(
    Guid CiId,
    string Name,
    string? AssetTag,
    string? SerialNumber,
    CiType Type,
    string Payload);

public enum CiLabelOutcome
{
    Success,
    NotFound,
    Invalid,
}

public sealed record CiLabelResult(
    CiLabelOutcome Outcome,
    byte[]? Content = null,
    string? FileName = null,
    IReadOnlyDictionary<string, string[]>? Errors = null,
    string? Error = null);
