using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Modules.Helpdesk.Data;

public sealed class TicketCategoryConfiguration : IEntityTypeConfiguration<TicketCategory>
{
    public void Configure(EntityTypeBuilder<TicketCategory> builder)
    {
        builder.ToTable("ticket_categories", "helpdesk");
        builder.HasKey(category => category.Id);
        builder.Property(category => category.Name).HasMaxLength(100).IsRequired();
        builder.HasOne(category => category.Parent).WithMany(category => category.Children)
            .HasForeignKey(category => category.ParentId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(category => new { category.ParentId, category.Name }).IsUnique();
        builder.HasIndex(category => new { category.IsActive, category.SortOrder });
    }
}

public sealed class TicketCustomFieldConfiguration : IEntityTypeConfiguration<TicketCustomField>
{
    public void Configure(EntityTypeBuilder<TicketCustomField> builder)
    {
        builder.ToTable("ticket_custom_fields", "helpdesk");
        builder.HasKey(field => field.Id);
        builder.Property(field => field.Key).HasMaxLength(50).IsRequired();
        builder.Property(field => field.Label).HasMaxLength(100).IsRequired();
        builder.Property(field => field.Type).HasConversion<string>().HasMaxLength(16);
        builder.Property(field => field.Options).HasColumnType("text[]").IsRequired();
        builder.HasOne(field => field.Category).WithMany(category => category.Fields)
            .HasForeignKey(field => field.CategoryId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(field => new { field.CategoryId, field.Key }).IsUnique();
    }
}

public sealed class TicketCustomFieldValueConfiguration : IEntityTypeConfiguration<TicketCustomFieldValue>
{
    public void Configure(EntityTypeBuilder<TicketCustomFieldValue> builder)
    {
        builder.ToTable("ticket_custom_field_values", "helpdesk");
        builder.HasKey(value => value.Id);
        builder.Property(value => value.Value).HasMaxLength(1_000).IsRequired();
        builder.HasOne(value => value.Ticket).WithMany(ticket => ticket.CustomFieldValues)
            .HasForeignKey(value => value.TicketId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(value => value.Field).WithMany()
            .HasForeignKey(value => value.FieldId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(value => new { value.TicketId, value.FieldId }).IsUnique();
    }
}
