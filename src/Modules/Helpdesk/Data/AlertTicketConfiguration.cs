using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Modules.Helpdesk.Data;

public sealed class AlertTicketConfiguration : IEntityTypeConfiguration<AlertTicket>
{
    public void Configure(EntityTypeBuilder<AlertTicket> builder)
    {
        builder.ToTable("alert_tickets", "helpdesk");
        builder.HasKey(entry => entry.Id);

        // The whole point of the table. Two consumers racing on one rule cannot both open a ticket:
        // the loser's transaction fails and its message is retried against the state the winner left,
        // exactly as WP-3.5's filtered unique index does for the alert itself.
        builder.Property(entry => entry.DedupeKey).HasMaxLength(300).IsRequired();
        builder.HasIndex(entry => entry.DedupeKey).IsUnique();

        builder.Property(entry => entry.RuleId).HasMaxLength(200).IsRequired();
        builder.Property(entry => entry.LastSeverity).HasMaxLength(16).IsRequired();

        // Nullable: a raise the breaker refused still writes a row, so the storm is legible afterwards.
        builder.HasOne(entry => entry.Ticket).WithMany()
            .HasForeignKey(entry => entry.TicketId)
            .OnDelete(DeleteBehavior.SetNull);

        // The circuit breaker's durable fallback counts rows through this index when Redis cannot answer.
        builder.HasIndex(entry => entry.TicketCreatedAt);
        builder.HasIndex(entry => entry.DeviceId);
    }
}
