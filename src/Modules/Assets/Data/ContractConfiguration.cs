using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Modules.Assets.Data;

public sealed class VendorConfiguration : IEntityTypeConfiguration<Vendor>
{
    public void Configure(EntityTypeBuilder<Vendor> builder)
    {
        builder.ToTable("vendors", "assets");
        builder.HasKey(vendor => vendor.Id);
        builder.Property(vendor => vendor.Name).HasMaxLength(200).IsRequired();
        builder.Property(vendor => vendor.ContactName).HasMaxLength(200);
        builder.Property(vendor => vendor.ContactEmail).HasMaxLength(320);
        builder.Property(vendor => vendor.ContactPhone).HasMaxLength(50);
        builder.Property(vendor => vendor.Website).HasMaxLength(500);
        builder.Property(vendor => vendor.Notes).HasMaxLength(2_000);

        // Two vendor rows with the same name would make every contract list ambiguous, so the name is
        // the natural key; the service compares case-insensitively before the index ever sees it.
        builder.HasIndex(vendor => vendor.Name).IsUnique();
        builder.HasIndex(vendor => vendor.IsActive);
    }
}

public sealed class ContractConfiguration : IEntityTypeConfiguration<Contract>
{
    public void Configure(EntityTypeBuilder<Contract> builder)
    {
        builder.ToTable("contracts", "assets");
        builder.HasKey(contract => contract.Id);
        builder.Property(contract => contract.PoNumber).HasMaxLength(100).IsRequired();
        builder.Property(contract => contract.ContractNumber).HasMaxLength(100);
        builder.Property(contract => contract.DepartmentName).HasMaxLength(200);
        builder.Property(contract => contract.Name).HasMaxLength(200).IsRequired();
        builder.Property(contract => contract.Type).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(contract => contract.Cost).HasPrecision(18, 2);
        builder.Property(contract => contract.Currency).HasMaxLength(3);
        builder.Property(contract => contract.OwnerName).HasMaxLength(200);
        builder.Property(contract => contract.OwnerEmail).HasMaxLength(320);
        builder.Property(contract => contract.Notes).HasMaxLength(2_000);

        // A vendor with contracts still on the books is not deletable; the service turns the refusal
        // into a 409 that names what is in the way, exactly as the CI delete guard does.
        builder.HasOne(contract => contract.Vendor).WithMany(vendor => vendor.Contracts)
            .HasForeignKey(contract => contract.VendorId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(contract => contract.PoNumber).IsUnique();
        builder.HasIndex(contract => contract.VendorId);
        builder.HasIndex(contract => contract.EndDate);
    }
}

public sealed class ContractNotificationConfiguration : IEntityTypeConfiguration<ContractNotification>
{
    public void Configure(EntityTypeBuilder<ContractNotification> builder)
    {
        builder.ToTable("contract_notifications", "assets");
        builder.HasKey(notification => notification.Id);
        builder.Property(notification => notification.Subject).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(notification => notification.SubjectName).HasMaxLength(200).IsRequired();
        // Wide enough for the configured recipient list joined end to end — the settings cap times the
        // longest address each may be — so a notice records everyone it went to rather than a prefix.
        builder.Property(notification => notification.Recipient).HasMaxLength(1300).IsRequired();
        builder.Property(notification => notification.Message).HasMaxLength(500).IsRequired();

        // The dedupe key. It carries the due date so a renewal — a new end date — starts a fresh
        // 30/7/0 cycle instead of being silenced by the notices raised against the old one.
        builder.HasIndex(notification => new
            {
                notification.Subject,
                notification.SubjectId,
                notification.DueDate,
                notification.ThresholdDays,
            })
            .IsUnique();
        builder.HasIndex(notification => notification.SentAt);
    }
}
