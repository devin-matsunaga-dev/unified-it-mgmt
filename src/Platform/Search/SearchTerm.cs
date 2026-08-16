using System.Text;

namespace Platform.Search;

/// <summary>
/// Turns what somebody has typed into the two things a source can query with: a Postgres tsquery for the
/// prose columns, and an escaped literal for the columns full-text search cannot be trusted with.
/// <para>
/// This lived in <c>Modules.Helpdesk.Features.Tickets.TicketSearchQuery</c> until WP-5.4, which needed the
/// same arithmetic in five places. It moved here rather than being copied, because a search box that
/// tokenises one way for tickets and another way for assets is a box whose results depend on which group
/// you were looking at.
/// </para>
/// </summary>
public static class SearchTerm
{
    /// <summary>The text-search dictionary every generated tsvector column in this solution is built with.</summary>
    public const string Configuration = "english";

    /// <summary>
    /// Builds an AND-ed prefix tsquery, or null when the input holds nothing searchable. Every term is a
    /// prefix match (<c>auro:*</c>), so results narrow while typing instead of only appearing on the whole
    /// word. Operator characters are dropped rather than escaped: the input is a search box, not a query
    /// language, and a stray <c>&amp;</c> must not be able to make Postgres raise a syntax error.
    /// </summary>
    public static string? ToPrefixTsQuery(string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return null;
        }

        var terms = new List<string>();
        var term = new StringBuilder();
        foreach (var character in search)
        {
            if (char.IsLetterOrDigit(character))
            {
                term.Append(char.ToLowerInvariant(character));
            }
            else if (term.Length > 0)
            {
                terms.Add(term.ToString());
                term.Clear();
            }
        }

        if (term.Length > 0)
        {
            terms.Add(term.ToString());
        }

        return terms.Count == 0 ? null : string.Join(" & ", terms.Select(item => $"{item}:*"));
    }

    /// <summary>
    /// The typed text as an identifier, if it could be one: trimmed, and only when it is a single
    /// unbroken token of a plausible length.
    /// <para>
    /// This exists because the full-text parser is not safe to look up an identifier with, and the failure
    /// is silent. <c>10.10.0.5</c> parses to the single lexeme <c>10.10.0.5</c>, while
    /// <see cref="ToPrefixTsQuery"/> splits the same input into <c>10:* &amp; 10:* &amp; 0:* &amp; 5:*</c> —
    /// and no lexeme begins <c>0</c>, so <b>searching a device for its own IP address matches nothing</b>.
    /// Punctuated serial numbers can split the same way. Every source that holds a column somebody quotes
    /// verbatim therefore matches it directly as well, which is the shape WP-1.10 already used for a
    /// ticket number.
    /// </para>
    /// </summary>
    public static string? ToIdentifier(string? search)
    {
        var trimmed = search?.Trim();
        return trimmed is { Length: > 0 and <= 128 } && !trimmed.Any(char.IsWhiteSpace) ? trimmed : null;
    }

    /// <summary>
    /// The same token with the <c>LIKE</c> wildcards neutralised, for an <c>ILIKE</c> comparison.
    /// <para>
    /// Without this a term containing <c>%</c> is a wildcard rather than a character: searching
    /// <c>50%</c> would match every asset tag beginning "50". The backslash goes first, because escaping it
    /// afterwards would escape the escapes this method just added.
    /// </para>
    /// </summary>
    public static string EscapeLike(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);
}
