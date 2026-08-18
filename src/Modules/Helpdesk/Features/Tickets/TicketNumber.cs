using Modules.Helpdesk.Data;

namespace Modules.Helpdesk.Features.Tickets;

/// <summary>
/// The one place a ticket number is spelled. Incidents keep <c>INC-</c>; service requests read
/// <c>REQ-</c> so an alert-raised incident is never mistaken for something somebody asked for.
/// <para>
/// <b>The digits are the ticket, not the prefix.</b> <c>SequenceNumber</c> is a single identity column
/// shared by both kinds, so <c>INC-000042</c> and <c>REQ-000042</c> can never both exist — 42 is one
/// ticket, and the prefix only says which kind it is. Two consequences worth knowing: a service
/// request's numbers are gappy (REQ-000003 may be followed by REQ-000009), and searching by number
/// can ignore the prefix entirely, which <see cref="TicketSearchQuery.ToSequenceNumber"/> already did.
/// </para>
/// <para>
/// Because of that, changing a ticket's type changes how its number reads without renumbering it —
/// <c>Ticket.Number</c> is computed and <c>builder.Ignore</c>d, never stored — so nothing has to be
/// backfilled and no historical reference breaks.
/// </para>
/// </summary>
public static class TicketNumber
{
    public const string IncidentPrefix = "INC";

    /// <summary>
    /// "REQ" rather than "SVC": ITIL's term for the thing is a *request*, and in ITSM vocabulary "SVC"
    /// usually names a service — which this product also has, as CIs in the CMDB. One constant to
    /// change if a different house style is wanted.
    /// </summary>
    public const string ServiceRequestPrefix = "REQ";

    public static string PrefixFor(TicketType type) => type switch
    {
        TicketType.Incident => IncidentPrefix,
        TicketType.ServiceRequest => ServiceRequestPrefix,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown ticket type."),
    };

    public static string Format(TicketType type, long sequenceNumber) =>
        $"{PrefixFor(type)}-{sequenceNumber:000000}";
}
