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
        builder.Property(policy => policy.Category).HasMaxLength(100);
        builder.HasOne(policy => policy.Calendar).WithMany().HasForeignKey(policy => policy.CalendarId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(policy => new { policy.Priority, policy.Category, policy.IsActive });
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
        builder.HasIndex(sla => sla.TicketId).IsUnique();
    }
}
