using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Modules.Helpdesk.Data;

public sealed class TeamConfiguration : IEntityTypeConfiguration<Team>
{
    public void Configure(EntityTypeBuilder<Team> builder)
    {
        builder.HasKey(team => team.Id);
        builder.Property(team => team.Name).HasMaxLength(100).IsRequired();
        builder.HasIndex(team => team.Name).IsUnique();
    }
}

public sealed class TeamMemberConfiguration : IEntityTypeConfiguration<TeamMember>
{
    public void Configure(EntityTypeBuilder<TeamMember> builder)
    {
        builder.HasKey(member => new { member.TeamId, member.TechnicianId });
        builder.Property(member => member.TechnicianId).HasMaxLength(200);
        builder.HasOne(member => member.Team).WithMany(team => team.Members)
            .HasForeignKey(member => member.TeamId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class TicketQueueConfiguration : IEntityTypeConfiguration<TicketQueue>
{
    public void Configure(EntityTypeBuilder<TicketQueue> builder)
    {
        builder.HasKey(queue => queue.Id);
        builder.Property(queue => queue.Name).HasMaxLength(100).IsRequired();
        builder.Property(queue => queue.LastAssignedTechnicianId).HasMaxLength(200);
        builder.HasIndex(queue => new { queue.TeamId, queue.Name }).IsUnique();
        builder.HasOne(queue => queue.Team).WithMany().HasForeignKey(queue => queue.TeamId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class TicketAssignmentHistoryConfiguration : IEntityTypeConfiguration<TicketAssignmentHistory>
{
    public void Configure(EntityTypeBuilder<TicketAssignmentHistory> builder)
    {
        builder.HasKey(history => history.Id);
        builder.Property(history => history.FromTechnicianId).HasMaxLength(200);
        builder.Property(history => history.ToTechnicianId).HasMaxLength(200).IsRequired();
        builder.Property(history => history.ActorId).HasMaxLength(200).IsRequired();
        builder.Property(history => history.Kind).HasConversion<string>().HasMaxLength(16);
        builder.HasOne(history => history.Ticket).WithMany().HasForeignKey(history => history.TicketId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(history => history.Queue).WithMany().HasForeignKey(history => history.QueueId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(history => new { history.TicketId, history.OccurredAt });
    }
}
