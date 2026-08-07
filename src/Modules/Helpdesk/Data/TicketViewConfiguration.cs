using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Modules.Helpdesk.Data;

public sealed class TicketViewConfiguration : IEntityTypeConfiguration<TicketView>
{
    public void Configure(EntityTypeBuilder<TicketView> builder)
    {
        builder.ToTable("ticket_views", "helpdesk");
        builder.HasKey(view => view.Id);
        builder.Property(view => view.Name).HasMaxLength(100).IsRequired();
        builder.Property(view => view.OwnerId).HasMaxLength(200).IsRequired();
        builder.Property(view => view.OwnerDisplayName).HasMaxLength(200);
        builder.Property(view => view.FilterJson).HasColumnType("jsonb").IsRequired();
        builder.HasIndex(view => new { view.OwnerId, view.Name }).IsUnique();
        builder.HasIndex(view => view.IsShared);
    }
}
