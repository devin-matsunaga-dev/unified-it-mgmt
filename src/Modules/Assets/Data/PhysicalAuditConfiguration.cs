using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Modules.Assets.Data;

public sealed class PhysicalAuditSessionConfiguration : IEntityTypeConfiguration<PhysicalAuditSession>
{
    public void Configure(EntityTypeBuilder<PhysicalAuditSession> builder)
    {
        builder.ToTable("physical_audit_sessions", "assets");
        builder.HasKey(session => session.Id);

        builder.Property(session => session.Name).HasMaxLength(200).IsRequired();
        builder.Property(session => session.SiteName).HasMaxLength(200);
        builder.Property(session => session.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(session => session.OpenedBy).HasMaxLength(200).IsRequired();
        builder.Property(session => session.ClosedBy).HasMaxLength(200);
        builder.Property(session => session.Note).HasMaxLength(2_000);

        // The list's own query: the open sessions first, newest at the top.
        builder.HasIndex(session => new { session.Status, session.OpenedAt });

        builder.HasMany(session => session.Scans)
            .WithOne(scan => scan.Session)
            .HasForeignKey(scan => scan.SessionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class PhysicalAuditScanConfiguration : IEntityTypeConfiguration<PhysicalAuditScan>
{
    public void Configure(EntityTypeBuilder<PhysicalAuditScan> builder)
    {
        builder.ToTable("physical_audit_scans", "assets");
        builder.HasKey(scan => scan.Id);

        builder.Property(scan => scan.CiName).HasMaxLength(200).IsRequired();
        builder.Property(scan => scan.Code).HasMaxLength(500).IsRequired();
        builder.Property(scan => scan.ScannedBy).HasMaxLength(200).IsRequired();
        builder.Property(scan => scan.Note).HasMaxLength(2_000);

        // An asset is confirmed once per session however many people walk past it.
        builder.HasIndex(scan => new { scan.SessionId, scan.CiId }).IsUnique();

        // A scan is evidence about one asset, so it goes when the asset does — the rule WP-4.4 applied
        // to installed software and WP-4.2 to discovery facts, and the opposite of the Restrict guard on
        // a relationship, which is a fact about two peers rather than a property of one.
        builder.HasOne(scan => scan.Ci).WithMany()
            .HasForeignKey(scan => scan.CiId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
