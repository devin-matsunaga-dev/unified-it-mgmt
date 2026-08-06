using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Platform.Data;

public sealed class ConsumerDedupeEntryConfiguration : IEntityTypeConfiguration<ConsumerDedupeEntry>
{
    public void Configure(EntityTypeBuilder<ConsumerDedupeEntry> builder)
    {
        builder.ToTable("consumer_dedupe_entries");
        builder.HasKey(entry => entry.Key);
        builder.Property(entry => entry.Key).HasColumnName("dedupe_key").HasMaxLength(300);
        builder.Property(entry => entry.ConsumedAt).HasColumnName("consumed_at");
    }
}
