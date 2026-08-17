using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Modules.Helpdesk.Data;

public sealed class ProblemConfiguration : IEntityTypeConfiguration<Problem>
{
    public void Configure(EntityTypeBuilder<Problem> builder)
    {
        builder.ToTable("problems", "helpdesk");
        builder.HasKey(problem => problem.Id);

        // PRB-000012 the same way a ticket is INC-000012, from the database rather than from a count:
        // two people opening a problem at once must not both be told they made the first one.
        builder.Property(problem => problem.SequenceNumber).UseIdentityAlwaysColumn();
        builder.HasIndex(problem => problem.SequenceNumber).IsUnique();

        builder.Property(problem => problem.Title).HasMaxLength(200).IsRequired();
        builder.Property(problem => problem.Description).HasMaxLength(10_000).IsRequired();
        builder.Property(problem => problem.Status).HasConversion<string>().HasMaxLength(16);
        builder.Property(problem => problem.Priority).HasConversion<string>().HasMaxLength(16);
        builder.Property(problem => problem.RootCause).HasMaxLength(10_000);
        builder.Property(problem => problem.Workaround).HasMaxLength(10_000);
        builder.Property(problem => problem.Resolution).HasMaxLength(10_000);
        builder.Property(problem => problem.AssignedTechnicianId).HasMaxLength(200);
        builder.Property(problem => problem.OpenedById).HasMaxLength(200).IsRequired();
        builder.Property(problem => problem.OpenedByName).HasMaxLength(200).IsRequired();

        builder.HasOne(problem => problem.Category).WithMany()
            .HasForeignKey(problem => problem.CategoryId)
            .OnDelete(DeleteBehavior.Restrict);

        // The three reads this table gets: the board filtered by state, the known errors for one CI, and
        // the known errors for one category. The last two are what the detector asks on every pass.
        builder.HasIndex(problem => problem.Status);
        builder.HasIndex(problem => problem.CiId);
        builder.HasIndex(problem => problem.CategoryId);
        builder.Ignore(problem => problem.Number);
    }
}

public sealed class ProblemIncidentConfiguration : IEntityTypeConfiguration<ProblemIncident>
{
    public void Configure(EntityTypeBuilder<ProblemIncident> builder)
    {
        builder.ToTable("problem_incidents", "helpdesk");
        builder.HasKey(incident => incident.Id);

        builder.HasOne(incident => incident.Problem).WithMany(problem => problem.Incidents)
            .HasForeignKey(incident => incident.ProblemId)
            .OnDelete(DeleteBehavior.Cascade);

        // Deleting a ticket takes its problem link with it, exactly as it takes its CI links: the link is
        // a statement about a ticket and cannot outlive one.
        builder.HasOne(incident => incident.Ticket).WithMany()
            .HasForeignKey(incident => incident.TicketId)
            .OnDelete(DeleteBehavior.Cascade);

        // One incident belongs to one problem at most. Not a unique index on the pair but on the ticket
        // alone: an incident with two causes is a triage error, and the accept path links in bulk where a
        // silent double-link would be invisible.
        builder.HasIndex(incident => incident.TicketId).IsUnique();
        builder.HasIndex(incident => incident.ProblemId);

        builder.Property(incident => incident.LinkedById).HasMaxLength(200).IsRequired();
        builder.Property(incident => incident.LinkedByName).HasMaxLength(200).IsRequired();
    }
}

public sealed class ProblemSuggestionConfiguration : IEntityTypeConfiguration<ProblemSuggestion>
{
    public void Configure(EntityTypeBuilder<ProblemSuggestion> builder)
    {
        builder.ToTable("problem_suggestions", "helpdesk");
        builder.HasKey(suggestion => suggestion.Id);

        builder.Property(suggestion => suggestion.Scope).HasConversion<string>().HasMaxLength(16);
        builder.Property(suggestion => suggestion.Status).HasConversion<string>().HasMaxLength(16);
        builder.Property(suggestion => suggestion.ResolvedById).HasMaxLength(200);
        builder.Property(suggestion => suggestion.ResolvedByName).HasMaxLength(200);
        builder.Property(suggestion => suggestion.DismissReason).HasMaxLength(1_000);

        builder.HasOne(suggestion => suggestion.Category).WithMany()
            .HasForeignKey(suggestion => suggestion.CategoryId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(suggestion => suggestion.CreatedProblem).WithMany()
            .HasForeignKey(suggestion => suggestion.CreatedProblemId)
            .OnDelete(DeleteBehavior.SetNull);

        // "One open suggestion per subject" made true by the database rather than by the order the pass
        // happens to run in — WP-5.6's call, restated. Two filtered indexes rather than one over a shared
        // subject column, because a CI id and a category id are different things that happen to be Guids
        // and the second is a real foreign key. Both are filtered on Open: an accepted suggestion and the
        // dismissal that preceded it are history, and history repeats.
        builder.HasIndex(suggestion => suggestion.CiId)
            .IsUnique()
            .HasFilter("status = 'Open' AND ci_id IS NOT NULL")
            .HasDatabaseName("ix_problem_suggestions_open_ci");
        builder.HasIndex(suggestion => suggestion.CategoryId)
            .IsUnique()
            .HasFilter("status = 'Open' AND category_id IS NOT NULL")
            .HasDatabaseName("ix_problem_suggestions_open_category");

        // How the detector finds the dismissal it has to respect, and how the inbox sorts.
        builder.HasIndex(suggestion => new { suggestion.Status, suggestion.DetectedAt });
        builder.Ignore(suggestion => suggestion.SubjectId);
    }
}
