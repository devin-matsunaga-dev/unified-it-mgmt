using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Modules.Helpdesk.Data;

public sealed class TicketCiLinkConfiguration : IEntityTypeConfiguration<TicketCiLink>
{
    public void Configure(EntityTypeBuilder<TicketCiLink> builder)
    {
        builder.ToTable("ticket_ci_links", "helpdesk");
        builder.HasKey(link => link.Id);
        builder.Property(link => link.LinkedById).HasMaxLength(200).IsRequired();
        builder.Property(link => link.LinkedByName).HasMaxLength(200).IsRequired();

        builder.HasOne(link => link.Ticket).WithMany()
            .HasForeignKey(link => link.TicketId)
            .OnDelete(DeleteBehavior.Cascade);

        // One link per (ticket, CI): linking the same asset twice is the same fact, not a second one.
        builder.HasIndex(link => new { link.TicketId, link.CiId }).IsUnique();

        // The asset 360° page and the CI delete guard both look the other way down this index.
        builder.HasIndex(link => link.CiId);
    }
}
