using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Modules.Helpdesk.Data;

public sealed class TicketConfiguration : IEntityTypeConfiguration<Ticket>
{
    public void Configure(EntityTypeBuilder<Ticket> builder)
    {
        builder.ToTable("tickets", "helpdesk");
        builder.HasKey(ticket => ticket.Id);
        builder.Property(ticket => ticket.SequenceNumber).UseIdentityAlwaysColumn();
        builder.HasIndex(ticket => ticket.SequenceNumber).IsUnique();
        builder.Property(ticket => ticket.Title).HasMaxLength(200).IsRequired();
        builder.Property(ticket => ticket.Description).HasMaxLength(10_000).IsRequired();
        builder.Property(ticket => ticket.Type).HasConversion<string>().HasMaxLength(32);
        builder.Property(ticket => ticket.Urgency).HasConversion<string>().HasMaxLength(16);
        builder.Property(ticket => ticket.Impact).HasConversion<string>().HasMaxLength(16);
        builder.Property(ticket => ticket.Priority).HasConversion<string>().HasMaxLength(16);
        builder.Property(ticket => ticket.RequesterId).HasMaxLength(200).IsRequired();
        builder.Ignore(ticket => ticket.Number);
    }
}
