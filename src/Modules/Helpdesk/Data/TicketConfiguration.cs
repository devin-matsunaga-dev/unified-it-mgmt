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
        builder.Property(ticket => ticket.StatusId).HasDefaultValue(DefaultTicketStatuses.NewId);
        builder.HasOne(ticket => ticket.Status).WithMany().HasForeignKey(ticket => ticket.StatusId).OnDelete(DeleteBehavior.Restrict);
        builder.Property(ticket => ticket.RequesterId).HasMaxLength(200).IsRequired();
        builder.Property(ticket => ticket.RequesterDisplayName).HasMaxLength(200);
        builder.Property(ticket => ticket.RequesterEmail).HasMaxLength(320);
        builder.Property(ticket => ticket.AssignedTechnicianId).HasMaxLength(200);
        builder.HasOne(ticket => ticket.Queue).WithMany().HasForeignKey(ticket => ticket.QueueId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(ticket => ticket.Category).WithMany().HasForeignKey(ticket => ticket.CategoryId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(ticket => ticket.AssignedTechnicianId);
        builder.HasIndex(ticket => ticket.CategoryId);
        builder.Ignore(ticket => ticket.Number);
    }
}
