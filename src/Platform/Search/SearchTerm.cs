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

    /// <summary>
    /// Turns a piece of prose — a ticket's subject and body — into an OR-ed tsquery for finding documents
    /// that are <em>like</em> it (WP-5.9).
    /// <para>
    /// Deliberately OR and not the AND that <see cref="ToPrefixTsQuery"/> builds. That one serves a search
    /// box, where every word somebody types is a narrowing they meant. This one serves a paragraph nobody
    /// typed as a query: AND-ing forty words of a bug report against an article matches nothing, every time.
    /// What ranks the results is <c>ts_rank</c> over the weighted vector, so an article matching three of
    /// the words in its title beats one matching one word in its body.
    /// </para>
    /// <para>
    /// Whole words rather than prefixes, again unlike the search box: nothing is being typed <em>now</em>,
    /// so a prefix would only widen a query that is already wide. Terms shorter than three characters and a
    /// small stop list go, because "the" and "is" match every article there is and would flatten the ranking
    /// into noise. Postgres's own English dictionary drops most of them too — this is about not sending
    /// forty lexemes when eight carry the meaning.
    /// </para>
    /// </summary>
    /// <param name="maximumTerms">
    /// How many distinct words are sent, longest first. A cap rather than the whole document because a
    /// tsquery grows with it and the tail of a long description is padding — and because an unbounded
    /// query is one a caller can make expensive by pasting a book into a ticket.
    /// </param>
    public static string? ToSimilarityTsQuery(string? text, int maximumTerms = 24)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var terms = new List<string>();
        var term = new StringBuilder();
        foreach (var character in text)
        {
            if (char.IsLetterOrDigit(character))
            {
                term.Append(char.ToLowerInvariant(character));
            }
            else
            {
                Flush(term, terms);
            }
        }

        Flush(term, terms);

        // Longest first, then alphabetically. Length is a crude proxy for how much a word narrows, and the
        // tiebreak is what makes the same ticket always produce the same query — the property WP-5.7's
        // draft symptoms needed for the same reason.
        var chosen = terms
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(word => word.Length)
            .ThenBy(word => word, StringComparer.Ordinal)
            .Take(Math.Max(maximumTerms, 1))
            .ToList();

        return chosen.Count == 0 ? null : string.Join(" | ", chosen);
    }

    private static void Flush(StringBuilder term, List<string> terms)
    {
        if (term.Length > 0)
        {
            var word = term.ToString();
            if (word.Length >= 3 && !StopWords.Contains(word))
            {
                terms.Add(word);
            }

            term.Clear();
        }
    }

    /// <summary>
    /// The words that appear in every ticket ever written. Short on purpose: Postgres's English dictionary
    /// already removes the classic stop words, and this list exists for the ones a helpdesk repeats that it
    /// does not — "please", "issue", "problem" — which would otherwise be the top-ranked term in a query.
    /// </summary>
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "and", "are", "but", "can", "cannot", "for", "from", "get", "getting", "has", "have", "his", "her",
        "issue", "issues", "not", "please", "problem", "problems", "she", "that", "the", "them", "then",
        "there", "these", "they", "this", "those", "was", "were", "what", "when", "why", "will", "with",
        "would", "you", "your",
    };
}
