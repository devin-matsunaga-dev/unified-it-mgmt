using Modules.Helpdesk.Data;
using Modules.Helpdesk.Features.Tickets;

namespace Infrastructure.Tests;

/// <summary>
/// Incidents keep INC- so nothing an alert raised changes its name; service requests read REQ- so the
/// two are told apart at a glance in the list.
/// </summary>
public sealed class TicketNumberTests
{
    [Fact]
    public void Format_AnIncident_KeepsTheIncidentPrefix()
    {
        Assert.Equal("INC-000042", TicketNumber.Format(TicketType.Incident, 42));
    }

    [Fact]
    public void Format_AServiceRequest_ReadsAsARequest()
    {
        Assert.Equal("REQ-000042", TicketNumber.Format(TicketType.ServiceRequest, 42));
    }

    /// <summary>Six digits is the house style, shared with PRB- and KB-, and it does not truncate.</summary>
    [Theory]
    [InlineData(1, "INC-000001")]
    [InlineData(999_999, "INC-999999")]
    [InlineData(1_000_000, "INC-1000000")]
    public void Format_PadsToSixDigitsAndGrowsBeyondThem(long sequenceNumber, string expected)
    {
        Assert.Equal(expected, TicketNumber.Format(TicketType.Incident, sequenceNumber));
    }

    /// <summary>
    /// The number a ticket answers to follows its type with nothing stored and nothing renumbered —
    /// <c>Ticket.Number</c> is computed, so re-typing a ticket re-reads it and the digits never move.
    /// </summary>
    [Fact]
    public void TicketNumber_FollowsTheTypeWithoutChangingTheSequence()
    {
        var ticket = new Ticket
        {
            Id = Guid.CreateVersion7(),
            SequenceNumber = 7,
            Title = "Laptop will not charge",
            Description = "It does not charge.",
            Type = TicketType.Incident,
            RequesterId = "enduser1",
        };

        Assert.Equal("INC-000007", ticket.Number);

        ticket.Type = TicketType.ServiceRequest;

        Assert.Equal("REQ-000007", ticket.Number);
    }

    /// <summary>FAILURE PATH: an unmapped type must be loud rather than silently prefixless.</summary>
    [Fact]
    public void PrefixFor_ATypeThatIsNotMapped_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TicketNumber.PrefixFor((TicketType)99));
    }

    /// <summary>
    /// The digits are the ticket. Both spellings of 42 parse to the same sequence, so pasting a number
    /// into search works whichever prefix the person copied — including an old INC- for what is now a
    /// service request.
    /// </summary>
    [Theory]
    [InlineData("INC-000042")]
    [InlineData("REQ-000042")]
    [InlineData("42")]
    public void ToSequenceNumber_IgnoresThePrefix(string search)
    {
        Assert.Equal(42, TicketSearchQuery.ToSequenceNumber(search));
    }
}
