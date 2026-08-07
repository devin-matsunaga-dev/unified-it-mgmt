using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Modules.Assets.Data;

public sealed class CiLifecycleTransitionConfiguration : IEntityTypeConfiguration<CiLifecycleTransition>
{
    /// <summary>
    /// The legal graph. The forward chain is Ordered→InStock→Deployed→InRepair→Retired→Disposed;
    /// the extra edges are the real returns (a repaired laptop goes back out, a deployed one comes
    /// back to stock). Anything absent — notably Ordered→Disposed — is rejected with a 409.
    /// </summary>
    private static readonly (CiLifecycleState From, CiLifecycleState To)[] Allowed =
    [
        (CiLifecycleState.Ordered, CiLifecycleState.InStock),
        (CiLifecycleState.InStock, CiLifecycleState.Deployed),
        (CiLifecycleState.InStock, CiLifecycleState.InRepair),
        (CiLifecycleState.InStock, CiLifecycleState.Retired),
        (CiLifecycleState.Deployed, CiLifecycleState.InStock),
        (CiLifecycleState.Deployed, CiLifecycleState.InRepair),
        (CiLifecycleState.Deployed, CiLifecycleState.Retired),
        (CiLifecycleState.InRepair, CiLifecycleState.Deployed),
        (CiLifecycleState.InRepair, CiLifecycleState.InStock),
        (CiLifecycleState.InRepair, CiLifecycleState.Retired),
        (CiLifecycleState.Retired, CiLifecycleState.Disposed),
    ];

    public void Configure(EntityTypeBuilder<CiLifecycleTransition> builder)
    {
        builder.ToTable("ci_lifecycle_transitions", "assets");
        builder.HasKey(transition => new { transition.FromState, transition.ToState });
        builder.Property(transition => transition.FromState).HasConversion<string>().HasMaxLength(20);
        builder.Property(transition => transition.ToState).HasConversion<string>().HasMaxLength(20);
        builder.HasData(Allowed.Select(edge => new CiLifecycleTransition { FromState = edge.From, ToState = edge.To }));
    }
}

public sealed class CiLifecycleHistoryConfiguration : IEntityTypeConfiguration<CiLifecycleHistory>
{
    public void Configure(EntityTypeBuilder<CiLifecycleHistory> builder)
    {
        builder.ToTable("ci_lifecycle_history", "assets");
        builder.HasKey(history => history.Id);
        builder.Property(history => history.FromState).HasConversion<string>().HasMaxLength(20);
        builder.Property(history => history.ToState).HasConversion<string>().HasMaxLength(20);
        builder.Property(history => history.Note).HasMaxLength(1_000);
        builder.Property(history => history.ActorId).HasMaxLength(200).IsRequired();
        builder.HasOne(history => history.Ci).WithMany().HasForeignKey(history => history.CiId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(history => new { history.CiId, history.OccurredAt });
    }
}

public sealed class CiAssignmentEntryConfiguration : IEntityTypeConfiguration<CiAssignmentEntry>
{
    public void Configure(EntityTypeBuilder<CiAssignmentEntry> builder)
    {
        builder.ToTable("ci_assignments", "assets");
        builder.HasKey(entry => entry.Id);
        builder.Property(entry => entry.Action).HasConversion<string>().HasMaxLength(20);
        // Owner, department, and site names are snapshots: the rows they came from live in the
        // platform schema, which this module may not join to, and the log must stay readable after
        // a person leaves the directory.
        builder.Property(entry => entry.FromOwnerName).HasMaxLength(200);
        builder.Property(entry => entry.ToOwnerName).HasMaxLength(200);
        builder.Property(entry => entry.DepartmentName).HasMaxLength(200);
        builder.Property(entry => entry.SiteName).HasMaxLength(200);
        builder.Property(entry => entry.Note).HasMaxLength(1_000);
        builder.Property(entry => entry.ActorId).HasMaxLength(200).IsRequired();
        builder.HasOne(entry => entry.Ci).WithMany().HasForeignKey(entry => entry.CiId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(entry => new { entry.CiId, entry.OccurredAt });
    }
}
