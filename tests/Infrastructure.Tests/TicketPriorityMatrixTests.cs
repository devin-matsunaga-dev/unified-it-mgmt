using Modules.Helpdesk.Data;
using Modules.Helpdesk.Features.Tickets;

namespace Infrastructure.Tests;

public sealed class TicketPriorityMatrixTests
{
    [Theory]
    [InlineData(TicketLevel.Low, TicketLevel.Low, TicketPriority.Low)]
    [InlineData(TicketLevel.Low, TicketLevel.High, TicketPriority.Medium)]
    [InlineData(TicketLevel.High, TicketLevel.Medium, TicketPriority.High)]
    [InlineData(TicketLevel.High, TicketLevel.High, TicketPriority.Critical)]
    public void Calculate_UrgencyAndImpact_ReturnsMatrixPriority(
        TicketLevel urgency,
        TicketLevel impact,
        TicketPriority expected)
    {
        Assert.Equal(expected, TicketPriorityMatrix.Calculate(urgency, impact));
    }
}
