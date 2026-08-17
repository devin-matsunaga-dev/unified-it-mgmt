using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Modules.Assets.Data;

public sealed class ChangeRequestConfiguration : IEntityTypeConfiguration<ChangeRequest>
{
    public void Configure(EntityTypeBuilder<ChangeRequest> builder)
    {
        builder.ToTable("change_requests", "assets");
        builder.HasKey(request => request.Id);

        builder.Property(request => request.SequenceNumber).UseIdentityAlwaysColumn();
        builder.HasIndex(request => request.SequenceNumber).IsUnique();

        builder.Property(request => request.Title).HasMaxLength(200).IsRequired();
        builder.Property(request => request.Description).HasMaxLength(10_000).IsRequired();
        builder.Property(request => request.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(request => request.RequestedById).HasMaxLength(200).IsRequired();
        builder.Property(request => request.RequestedByName).HasMaxLength(200).IsRequired();
        builder.Property(request => request.DecidedById).HasMaxLength(200);
        builder.Property(request => request.DecidedByName).HasMaxLength(200);
        builder.Property(request => request.DecisionNote).HasMaxLength(2_000);

        // The calendar's own query: everything scheduled inside a month, whatever state it is in.
        builder.HasIndex(request => new { request.PlannedStartAt, request.PlannedEndAt });

        // And the board's: what is waiting for a decision, soonest first.
        builder.HasIndex(request => new { request.Status, request.PlannedStartAt });

        builder.Ignore(request => request.Number);

        builder.HasMany(request => request.Cis)
            .WithOne(scope => scope.ChangeRequest)
            .HasForeignKey(scope => scope.ChangeRequestId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class ChangeRequestCiConfiguration : IEntityTypeConfiguration<ChangeRequestCi>
{
    public void Configure(EntityTypeBuilder<ChangeRequestCi> builder)
    {
        builder.ToTable("change_request_cis", "assets");

        // A CI is on a change once. It cannot be both named and a dependent — being named wins, because
        // that is the statement somebody actually made.
        builder.HasKey(scope => new { scope.ChangeRequestId, scope.CiId });

        // Restrict rather than cascade, unlike a physical-audit scan: a scan is evidence about an asset
        // and goes with it, while a CI listed on an agreed change is half of an agreement two parties
        // made. Deleting it out from under an approved window would leave the window covering a device
        // whose CI no longer explains why. WP-2.3's relationship guard, for the same reason.
        builder.HasOne(scope => scope.Ci).WithMany()
            .HasForeignKey(scope => scope.CiId)
            .OnDelete(DeleteBehavior.Restrict);

        // "Which changes touch this CI", which is what the CI page and the delete guard both ask.
        builder.HasIndex(scope => scope.CiId);
    }
}
