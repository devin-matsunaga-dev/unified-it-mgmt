using Platform.Search;

namespace Infrastructure.Tests;

/// <summary>
/// The tokeniser every source shares (WP-5.4). <see cref="TicketSearchQueryTests"/> covers the same prefix
/// arithmetic through Helpdesk's name for it, which is what proves the delegation still holds; what is
/// here is the part WP-5.4 added — the identifier path that exists because full-text search cannot be
/// trusted with an address or a punctuated serial.
/// </summary>
public sealed class SearchTermTests
{
    [Theory]
    [InlineData("core", "core:*")]
    [InlineData("DC1 core switch", "dc1:* & core:* & switch:*")]
    [InlineData("  NET-0002  ", "net:* & 0002:*")]
    public void ToPrefixTsQuery_Terms_BecomeAndedPrefixMatches(string search, string expected) =>
        Assert.Equal(expected, SearchTerm.ToPrefixTsQuery(search));

    /// <summary>
    /// The reason the identifier path exists at all, stated as a test so nobody removes it as a duplicate
    /// of the tsquery: an address does not survive the round trip. Postgres keeps <c>10.10.0.5</c> as one
    /// lexeme; the search box splits it into four prefix terms, and nothing in that vector begins "0".
    /// </summary>
    [Fact]
    public void ToPrefixTsQuery_AnIpAddress_SplitsIntoTermsThatCannotMatchItsOwnLexeme()
    {
        Assert.Equal("10:* & 10:* & 0:* & 5:*", SearchTerm.ToPrefixTsQuery("10.10.0.5"));
        Assert.Equal("10.10.0.5", SearchTerm.ToIdentifier("10.10.0.5"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("&|!():*")]
    public void ToPrefixTsQuery_NothingSearchable_ReturnsNull(string? search) =>
        Assert.Null(SearchTerm.ToPrefixTsQuery(search));

    [Theory]
    [InlineData("FTX2401R001", "FTX2401R001")]
    [InlineData("  NET-0002 ", "NET-0002")]
    [InlineData("enduser3@example.test", "enduser3@example.test")]
    public void ToIdentifier_ASingleToken_IsKeptVerbatimApartFromTheSpaces(string search, string expected) =>
        Assert.Equal(expected, SearchTerm.ToIdentifier(search));

    /// <summary>
    /// Several words is a phrase somebody is searching, not a serial they are quoting. Matching a phrase
    /// against an identifier column can never succeed and would cost a scan per source per keystroke.
    /// </summary>
    [Theory]
    [InlineData("core switch")]
    [InlineData(null)]
    [InlineData("   ")]
    public void ToIdentifier_WithoutASingleToken_ReturnsNull(string? search) =>
        Assert.Null(SearchTerm.ToIdentifier(search));

    /// <summary>
    /// The failure path: an unescaped <c>%</c> is a wildcard, so searching "50%" would ILIKE-match every
    /// asset tag beginning "50" and report them as exact identifier hits.
    /// </summary>
    [Theory]
    [InlineData("50%", "50\\%")]
    [InlineData("a_b", "a\\_b")]
    [InlineData("back\\slash", "back\\\\slash")]
    [InlineData("100%_\\", "100\\%\\_\\\\")]
    public void EscapeLike_WildcardCharacters_AreNeutralised(string value, string expected) =>
        Assert.Equal(expected, SearchTerm.EscapeLike(value));
}
