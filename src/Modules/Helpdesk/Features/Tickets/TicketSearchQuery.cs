using System.Globalization;

using Platform.Search;

namespace Modules.Helpdesk.Features.Tickets;

/// <summary>
/// Turns what an agent has typed so far into the things a ticket query needs: a Postgres tsquery, and the
/// ticket number they may have pasted.
/// </summary>
public static class TicketSearchQuery
{
    /// <summary>
    /// Builds an AND-ed prefix tsquery, or null when the input holds nothing searchable.
    /// <para>
    /// The arithmetic moved to <see cref="SearchTerm.ToPrefixTsQuery"/> in WP-5.4, which needed it in five
    /// places. This stays as the ticket vocabulary's own name for it, and delegates rather than copies:
    /// two tokenisers would eventually disagree, and the disagreement would show up as the ticket list and
    /// the global search box returning different tickets for the same word.
    /// </para>
    /// </summary>
    public static string? ToPrefixTsQuery(string? search) => SearchTerm.ToPrefixTsQuery(search);

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
