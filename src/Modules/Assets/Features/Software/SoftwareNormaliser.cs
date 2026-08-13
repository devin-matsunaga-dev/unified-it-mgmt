using System.Text;

using Modules.Assets.Data;

namespace Modules.Assets.Features.Software;

/// <summary>One catalogue rule, flattened so the matcher needs no database and no EF types.</summary>
public sealed record SoftwareRule(Guid ProductId, SoftwareMatchKind MatchKind, string Pattern, int Priority);

/// <summary>
/// The normalisation catalogue's one decision: which product a raw installed-software string means.
/// <para>
/// Pure, so the whole precedence matrix is testable without infrastructure. Rules are consulted most
/// specific kind first — an Exact rule always beats a Prefix, which always beats a Contains — because a
/// catalogue entry that names the whole string is a statement about that string, while a Contains rule
/// is a net. Within one kind the operator's priority decides, then the longer pattern, then the pattern
/// itself so the answer never depends on the order rows came back from the database.
/// </para>
/// <para>
/// The version is deliberately not part of any of this. A raw name carries its version in a different
/// place, spelling and language for every publisher, so v1 maps name → product and keeps the version
/// verbatim on the install row.
/// </para>
/// </summary>
public static class SoftwareNormaliser
{
    /// <summary>
    /// The comparable form of a raw name: trimmed, inner whitespace collapsed to one space, lower-cased
    /// invariantly. Both stored patterns and incoming names go through it, so "Microsoft  Office " and
    /// "microsoft office" are the same string by the time they are compared.
    /// </summary>
    public static string Canonicalise(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var result = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var character in value.Trim())
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = result.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                result.Append(' ');
                pendingSpace = false;
            }

            result.Append(char.ToLowerInvariant(character));
        }

        return result.ToString();
    }

    /// <summary>The product a raw name normalises to, or null when nothing in the catalogue claims it.</summary>
    public static Guid? Match(string? rawName, IEnumerable<SoftwareRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var candidate = Canonicalise(rawName);
        if (candidate.Length == 0)
        {
            return null;
        }

        return Ordered(rules)
            .Where(rule => Matches(rule, candidate))
            .Select(rule => (Guid?)rule.ProductId)
            .FirstOrDefault();
    }

    /// <summary>The order the matcher walks rules in, exposed so a test can assert precedence directly.</summary>
    public static IReadOnlyList<SoftwareRule> Ordered(IEnumerable<SoftwareRule> rules) =>
    [
        .. rules
            .Where(rule => rule.Pattern.Length > 0)
            .OrderBy(rule => (int)rule.MatchKind)
            .ThenBy(rule => rule.Priority)
            .ThenByDescending(rule => rule.Pattern.Length)
            .ThenBy(rule => rule.Pattern, StringComparer.Ordinal)
    ];

    private static bool Matches(SoftwareRule rule, string candidate) => rule.MatchKind switch
    {
        SoftwareMatchKind.Exact => string.Equals(candidate, rule.Pattern, StringComparison.Ordinal),
        SoftwareMatchKind.Prefix => candidate.StartsWith(rule.Pattern, StringComparison.Ordinal),
        SoftwareMatchKind.Contains => candidate.Contains(rule.Pattern, StringComparison.Ordinal),
        _ => false,
    };

    /// <summary>
    /// The dedupe key for an install within one CI. Two nullable columns cannot carry it in a unique
    /// index — Postgres treats two nulls as distinct — so the key is materialised into a column.
    /// </summary>
    public static string IdentityKeyFor(string rawName, string? version) =>
        $"{Canonicalise(rawName)}|{Canonicalise(version)}";
}
