using Modules.Helpdesk.Data;

namespace Modules.Helpdesk.Features.Tickets;

public static class TicketPriorityMatrix
{
    public static TicketPriority Calculate(TicketLevel urgency, TicketLevel impact) =>
        ((int)urgency, (int)impact) switch
        {
            (3, 3) => TicketPriority.Critical,
            (3, >= 2) or (>= 2, 3) => TicketPriority.High,
            (1, 1) => TicketPriority.Low,
            _ => TicketPriority.Medium,
        };
}
