using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Platform.Data;

public sealed class NotificationChannelConfiguration : IEntityTypeConfiguration<NotificationChannel>
{
    public void Configure(EntityTypeBuilder<NotificationChannel> builder)
    {
        builder.ToTable("notification_channels");
        builder.HasKey(channel => channel.Id);
        builder.Property(channel => channel.Id).ValueGeneratedNever();
        builder.Property(channel => channel.Name).HasMaxLength(200).IsRequired();
        builder.Property(channel => channel.Kind).HasConversion<string>().HasMaxLength(20).IsRequired();
        // Long enough for a Teams incoming-webhook URL, which carries two GUIDs and a signature.
        builder.Property(channel => channel.Target).HasMaxLength(2_000).IsRequired();
        builder.Property(channel => channel.Description).HasMaxLength(1_000);
        builder.Property(channel => channel.CreatedBy).HasMaxLength(200).IsRequired();
        builder.Property(channel => channel.UpdatedBy).HasMaxLength(200).IsRequired();
        builder.HasIndex(channel => channel.Name).IsUnique();
    }
}

public sealed class NotificationRoutingRuleConfiguration : IEntityTypeConfiguration<NotificationRoutingRule>
{
    public void Configure(EntityTypeBuilder<NotificationRoutingRule> builder)
    {
        builder.ToTable("notification_routing_rules");
        builder.HasKey(rule => rule.Id);
        builder.Property(rule => rule.Id).ValueGeneratedNever();
        builder.Property(rule => rule.Name).HasMaxLength(200).IsRequired();
        builder.Property(rule => rule.EventKind).HasMaxLength(100);
        builder.Property(rule => rule.MinimumSeverity).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(rule => rule.DeviceGroup).HasMaxLength(100);
        builder.Property(rule => rule.TimeZone).HasMaxLength(100).IsRequired();
        builder.Property(rule => rule.CreatedBy).HasMaxLength(200).IsRequired();
        builder.Property(rule => rule.UpdatedBy).HasMaxLength(200).IsRequired();
        builder.HasIndex(rule => rule.Name).IsUnique();
        builder.HasIndex(rule => rule.IsActive);
        // Restrict, not cascade: a channel that rules still point at is one somebody is relying on,
        // and silently deleting the routing with it is how a Critical alert stops reaching anyone.
        // Same shape as the WP-2.6 contract/vendor delete guards.
        builder.HasOne(rule => rule.Channel).WithMany(channel => channel.Rules)
            .HasForeignKey(rule => rule.ChannelId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class UserNotificationPreferenceConfiguration : IEntityTypeConfiguration<UserNotificationPreference>
{
    public void Configure(EntityTypeBuilder<UserNotificationPreference> builder)
    {
        builder.ToTable("user_notification_preferences");
        builder.HasKey(preference => preference.Id);
        builder.Property(preference => preference.Id).ValueGeneratedNever();
        builder.Property(preference => preference.UserId).HasMaxLength(200).IsRequired();
        builder.Property(preference => preference.EmailAddress).HasMaxLength(320);
        builder.Property(preference => preference.MinimumSeverity).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(preference => preference.TimeZone).HasMaxLength(100).IsRequired();
        builder.HasIndex(preference => preference.UserId).IsUnique();
    }
}

public sealed class NotificationDeliveryConfiguration : IEntityTypeConfiguration<NotificationDelivery>
{
    public void Configure(EntityTypeBuilder<NotificationDelivery> builder)
    {
        builder.ToTable("notification_deliveries");
        builder.HasKey(delivery => delivery.Id);
        builder.Property(delivery => delivery.Id).ValueGeneratedNever();
        builder.Property(delivery => delivery.EventKind).HasMaxLength(100).IsRequired();
        builder.Property(delivery => delivery.Severity).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(delivery => delivery.Subject).HasMaxLength(500).IsRequired();
        builder.Property(delivery => delivery.Body).HasMaxLength(8_000).IsRequired();
        builder.Property(delivery => delivery.DeepLink).HasMaxLength(2_000);
        builder.Property(delivery => delivery.DedupeKey).HasMaxLength(200);
        builder.Property(delivery => delivery.ChannelKind).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(delivery => delivery.TargetRedacted).HasMaxLength(500).IsRequired();
        builder.Property(delivery => delivery.UserId).HasMaxLength(200);
        builder.Property(delivery => delivery.Outcome).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(delivery => delivery.Detail).HasMaxLength(2_000);
        // A delivery is a dated record of what was attempted. It outlives the channel it names, the
        // same way an audit entry outlives its entity, so the channel goes to null rather than taking
        // its history with it.
        builder.HasOne(delivery => delivery.Channel).WithMany()
            .HasForeignKey(delivery => delivery.ChannelId).OnDelete(DeleteBehavior.SetNull);
        builder.HasIndex(delivery => delivery.OccurredAt);
        builder.HasIndex(delivery => new { delivery.Outcome, delivery.ReleaseAfter });
        builder.HasIndex(delivery => delivery.DedupeKey);
    }
}
