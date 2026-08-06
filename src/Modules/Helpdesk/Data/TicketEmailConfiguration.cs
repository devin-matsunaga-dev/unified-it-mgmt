using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Modules.Helpdesk.Data;

public sealed class TicketEmailConfiguration : IEntityTypeConfiguration<TicketEmail>
{
    public void Configure(EntityTypeBuilder<TicketEmail> builder)
    {
        builder.ToTable("ticket_emails", "helpdesk");
        builder.HasKey(email => email.Id);
        builder.Property(email => email.MessageId).HasMaxLength(998).IsRequired();
        builder.HasIndex(email => email.MessageId).IsUnique();
        builder.Property(email => email.Sender).HasMaxLength(320).IsRequired();
        builder.Property(email => email.Subject).HasMaxLength(998).IsRequired();
        builder.HasOne(email => email.Ticket).WithMany().HasForeignKey(email => email.TicketId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
