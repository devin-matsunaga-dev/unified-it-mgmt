using System.Globalization;
using System.Text;

namespace Modules.Helpdesk.Features.Tickets;

/// <summary>
/// Turns what an agent has typed so far into a Postgres tsquery. Every term is a prefix match
/// (<c>auro:*</c>), so results narrow while typing instead of only appearing on the whole word.
/// </summary>
public static class TicketSearchQuery
{
    /// <summary>
    /// Builds an AND-ed prefix tsquery, or null when the input holds nothing searchable. Operator
    /// characters are dropped rather than escaped: the input is a search box, not a query language.
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
    /// The ticket number a search refers to, if any: the list previously matched on it and agents
    /// paste numbers like "INC-000042" or just "42" into the search box.
    /// </summary>
    public static long? ToSequenceNumber(string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return null;
        }

        var digits = new string([.. search.Where(char.IsAsciiDigit)]);
        return digits.Length is > 0 and <= 18
            && long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            && number > 0
                ? number
                : null;
    }
}
