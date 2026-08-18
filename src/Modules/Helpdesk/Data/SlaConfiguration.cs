using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Modules.Helpdesk.Data;

public sealed class BusinessHoursCalendarConfiguration : IEntityTypeConfiguration<BusinessHoursCalendar>
{
    public void Configure(EntityTypeBuilder<BusinessHoursCalendar> builder)
    {
        builder.HasKey(calendar => calendar.Id);
        builder.Property(calendar => calendar.Name).HasMaxLength(100).IsRequired();
        builder.Property(calendar => calendar.TimeZoneId).HasMaxLength(100).IsRequired();
        builder.Property(calendar => calendar.WorkingDays).HasConversion<int>();
        builder.HasIndex(calendar => calendar.Name).IsUnique();
    }
}

public sealed class SlaPolicyConfiguration : IEntityTypeConfiguration<SlaPolicy>
{
    public void Configure(EntityTypeBuilder<SlaPolicy> builder)
    {
        builder.HasKey(policy => policy.Id);
        builder.Property(policy => policy.Name).HasMaxLength(100).IsRequired();
        builder.Property(policy => policy.Priority).HasConversion<string>().HasMaxLength(16);
        builder.Property(policy => policy.TicketType).HasConversion<string>().HasMaxLength(32);
        builder.HasOne(policy => policy.Calendar).WithMany().HasForeignKey(policy => policy.CalendarId)
            .OnDelete(DeleteBehavior.Restrict);
        // Restrict, not cascade: deleting a category must not quietly delete the SLA written for it.
        builder.HasOne(policy => policy.Category).WithMany().HasForeignKey(policy => policy.CategoryId)
            .OnDelete(DeleteBehavior.Restrict);
        // The order the matcher walks, which is the only index the hot path needs.
        builder.HasIndex(policy => new { policy.IsActive, policy.SortOrder });
    }
}

public sealed class TicketSlaConfiguration : IEntityTypeConfiguration<TicketSla>
{
    public void Configure(EntityTypeBuilder<TicketSla> builder)
    {
        builder.HasKey(sla => sla.Id);
        builder.HasOne(sla => sla.Ticket).WithOne().HasForeignKey<TicketSla>(sla => sla.TicketId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(sla => sla.Policy).WithMany().HasForeignKey(sla => sla.PolicyId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(sla => sla.Calendar).WithMany().HasForeignKey(sla => sla.CalendarId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(sla => sla.TicketId).IsUnique();
    }
}
