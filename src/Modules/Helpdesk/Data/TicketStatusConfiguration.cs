using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Modules.Helpdesk.Data;

public sealed class TicketStatusConfiguration : IEntityTypeConfiguration<TicketStatus>
{
    public void Configure(EntityTypeBuilder<TicketStatus> builder)
    {
        builder.ToTable("ticket_statuses", "helpdesk");
        builder.HasKey(status => status.Id);
        builder.Property(status => status.Name).HasMaxLength(50).IsRequired();
        builder.HasIndex(status => status.Name).IsUnique();
        builder.HasData(
            new TicketStatus { Id = DefaultTicketStatuses.NewId, Name = "New", DisplayOrder = 1 },
            new TicketStatus { Id = DefaultTicketStatuses.TriageId, Name = "Triage", DisplayOrder = 2 },
            new TicketStatus { Id = DefaultTicketStatuses.InProgressId, Name = "InProgress", DisplayOrder = 3 },
            new TicketStatus { Id = DefaultTicketStatuses.PendingId, Name = "Pending", DisplayOrder = 4 },
            new TicketStatus { Id = DefaultTicketStatuses.ResolvedId, Name = "Resolved", DisplayOrder = 5, RequiresResolutionNote = true },
            new TicketStatus { Id = DefaultTicketStatuses.ClosedId, Name = "Closed", DisplayOrder = 6 });
    }
}

public sealed class TicketStatusTransitionConfiguration : IEntityTypeConfiguration<TicketStatusTransition>
{
    public void Configure(EntityTypeBuilder<TicketStatusTransition> builder)
    {
        builder.ToTable("ticket_status_transitions", "helpdesk");
        builder.HasKey(transition => new { transition.FromStatusId, transition.ToStatusId });
        builder.HasOne(transition => transition.FromStatus).WithMany().HasForeignKey(transition => transition.FromStatusId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(transition => transition.ToStatus).WithMany().HasForeignKey(transition => transition.ToStatusId).OnDelete(DeleteBehavior.Restrict);
        builder.HasData(
            new TicketStatusTransition { FromStatusId = DefaultTicketStatuses.NewId, ToStatusId = DefaultTicketStatuses.TriageId },
            new TicketStatusTransition { FromStatusId = DefaultTicketStatuses.TriageId, ToStatusId = DefaultTicketStatuses.InProgressId },
            new TicketStatusTransition { FromStatusId = DefaultTicketStatuses.InProgressId, ToStatusId = DefaultTicketStatuses.PendingId },
            new TicketStatusTransition { FromStatusId = DefaultTicketStatuses.PendingId, ToStatusId = DefaultTicketStatuses.InProgressId },
            new TicketStatusTransition { FromStatusId = DefaultTicketStatuses.PendingId, ToStatusId = DefaultTicketStatuses.ResolvedId },
            new TicketStatusTransition { FromStatusId = DefaultTicketStatuses.ResolvedId, ToStatusId = DefaultTicketStatuses.ClosedId });
    }
}

public sealed class TicketTransitionHistoryConfiguration : IEntityTypeConfiguration<TicketTransitionHistory>
{
    public void Configure(EntityTypeBuilder<TicketTransitionHistory> builder)
    {
        builder.ToTable("ticket_transition_history", "helpdesk");
        builder.HasKey(history => history.Id);
        builder.Property(history => history.ResolutionNote).HasMaxLength(10_000);
        builder.Property(history => history.ActorId).HasMaxLength(200).IsRequired();
        builder.HasOne(history => history.Ticket).WithMany().HasForeignKey(history => history.TicketId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(history => history.FromStatus).WithMany().HasForeignKey(history => history.FromStatusId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(history => history.ToStatus).WithMany().HasForeignKey(history => history.ToStatusId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(history => new { history.TicketId, history.OccurredAt });
    }
}
