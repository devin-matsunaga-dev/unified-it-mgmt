using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Modules.Assets.Data;

public sealed class ContractReminderSettingsConfiguration : IEntityTypeConfiguration<ContractReminderSettings>
{
    public void Configure(EntityTypeBuilder<ContractReminderSettings> builder)
    {
        builder.ToTable("contract_reminder_settings", "assets");
        builder.HasKey(settings => settings.Id);
        builder.Property(settings => settings.Id).ValueGeneratedNever();
        // An array column, like the custom field options: the thresholds are a set with no meaning
        // apart from each other, and a row per number would need an order column to say the same.
        builder.Property(settings => settings.ThresholdDays).HasColumnType("integer[]").IsRequired();
        builder.Property(settings => settings.Recipients).HasColumnType("text[]").IsRequired();
        builder.Property(settings => settings.UpdatedBy).HasMaxLength(200).IsRequired();
    }
}
