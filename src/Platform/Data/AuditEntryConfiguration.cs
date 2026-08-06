using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Platform.Data;

public sealed class AuditEntryConfiguration : IEntityTypeConfiguration<AuditEntry>
{
    public void Configure(EntityTypeBuilder<AuditEntry> builder)
    {
        builder.ToTable("audit_entries", table =>
            table.HasCheckConstraint("ck_audit_entries_actor_id_not_empty", "length(actor_id) > 0"));
        builder.HasKey(entry => entry.Id);
        builder.Property(entry => entry.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(entry => entry.ActorId).HasColumnName("actor_id").HasMaxLength(200).IsRequired();
        builder.Property(entry => entry.Action).HasColumnName("action").HasMaxLength(100).IsRequired();
        builder.Property(entry => entry.EntityType).HasColumnName("entity_type").HasMaxLength(200).IsRequired();
        builder.Property(entry => entry.EntityId).HasColumnName("entity_id").HasMaxLength(200).IsRequired();
        builder.Property(entry => entry.BeforeJson).HasColumnName("before_json").HasColumnType("jsonb");
        builder.Property(entry => entry.AfterJson).HasColumnName("after_json").HasColumnType("jsonb");
        builder.Property(entry => entry.OccurredAt).HasColumnName("occurred_at").IsRequired();
        builder.Property(entry => entry.CorrelationId).HasColumnName("correlation_id").HasMaxLength(100).IsRequired();
        builder.HasIndex(entry => entry.OccurredAt).HasDatabaseName("ix_audit_entries_occurred_at");
        builder.HasIndex(entry => new { entry.EntityType, entry.EntityId })
            .HasDatabaseName("ix_audit_entries_entity_type_entity_id");
    }
}