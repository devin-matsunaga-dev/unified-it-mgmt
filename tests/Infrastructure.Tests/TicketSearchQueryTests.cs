using Modules.Helpdesk.Features.Tickets;

namespace Infrastructure.Tests;

public sealed class TicketSearchQueryTests
{
    [Theory]
    [InlineData("auro", "auro:*")]
    [InlineData("Printer Offline", "printer:* & offline:*")]
    [InlineData("  vpn   ", "vpn:*")]
    public void ToPrefixTsQuery_Terms_BecomePrefixMatches(string search, string expected) =>
        Assert.Equal(expected, TicketSearchQuery.ToPrefixTsQuery(search));

    [Theory]
    [InlineData("printer & !offline | (broken)", "printer:* & offline:* & broken:*")]
    [InlineData("it's :* 'quoted'", "it:* & s:* & quoted:*")]
    public void ToPrefixTsQuery_OperatorCharacters_AreDroppedNotEscaped(string search, string expected) =>
        Assert.Equal(expected, TicketSearchQuery.ToPrefixTsQuery(search));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("&|!():*")]
    public void ToPrefixTsQuery_NothingSearchable_ReturnsNull(string? search) =>
        Assert.Null(TicketSearchQuery.ToPrefixTsQuery(search));

    [Theory]
    [InlineData("INC-000042", 42L)]
    [InlineData("42", 42L)]
    [InlineData("#7", 7L)]
    public void ToSequenceNumber_TicketNumbers_AreRecognised(string search, long expected) =>
        Assert.Equal(expected, TicketSearchQuery.ToSequenceNumber(search));

    [Theory]
    [InlineData("printer")]
    [InlineData("INC-000000")]
    [InlineData("99999999999999999999")]
    [InlineData(null)]
    public void ToSequenceNumber_WithoutAUsableNumber_ReturnsNull(string? search) =>
        Assert.Null(TicketSearchQuery.ToSequenceNumber(search));
}
