using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Modules.Helpdesk.Data;

public sealed class TicketConfiguration : IEntityTypeConfiguration<Ticket>
{
    public void Configure(EntityTypeBuilder<Ticket> builder)
    {
        builder.ToTable("tickets", "helpdesk");
        builder.HasKey(ticket => ticket.Id);
        builder.Property(ticket => ticket.SequenceNumber).UseIdentityAlwaysColumn();
        builder.HasIndex(ticket => ticket.SequenceNumber).IsUnique();
        builder.Property(ticket => ticket.Title).HasMaxLength(200).IsRequired();
        builder.Property(ticket => ticket.Description).HasMaxLength(10_000).IsRequired();
        builder.Property(ticket => ticket.Type).HasConversion<string>().HasMaxLength(32);
        builder.Property(ticket => ticket.Urgency).HasConversion<string>().HasMaxLength(16);
        builder.Property(ticket => ticket.Impact).HasConversion<string>().HasMaxLength(16);
        builder.Property(ticket => ticket.Priority).HasConversion<string>().HasMaxLength(16);
        builder.Property(ticket => ticket.StatusId).HasDefaultValue(DefaultTicketStatuses.NewId);
        builder.HasOne(ticket => ticket.Status).WithMany().HasForeignKey(ticket => ticket.StatusId).OnDelete(DeleteBehavior.Restrict);
        builder.Property(ticket => ticket.RequesterId).HasMaxLength(200).IsRequired();
        builder.Property(ticket => ticket.RequesterDisplayName).HasMaxLength(200);
        builder.Property(ticket => ticket.RequesterEmail).HasMaxLength(320);
        builder.Property(ticket => ticket.AssignedTechnicianId).HasMaxLength(200);
        builder.HasOne(ticket => ticket.Queue).WithMany().HasForeignKey(ticket => ticket.QueueId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(ticket => ticket.Category).WithMany().HasForeignKey(ticket => ticket.CategoryId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(ticket => ticket.AssignedTechnicianId);
        builder.HasIndex(ticket => ticket.CategoryId);
        // WP-1.10 generated this over title and description alone. WP-5.4 widened it and weighted it, and
        // both halves are deliberate:
        //
        //  * The requester's name is in it because "requester name finds tickets" is WP-5.4's own
        //    verification step, and because there is only one answer to "what is a ticket searchable by" —
        //    a global search box that matched a name and a ticket list that did not would be one platform
        //    disagreeing with itself about the same word. The ticket list gains the same reach as a result.
        //  * setweight is what makes ts_rank prefer a title match to a passing mention in a description.
        //    Without it the ticket about the thing outranks the ticket the search was for as often as not.
        //
        // Written out rather than declared with HasGeneratedTsVectorColumn because that helper cannot
        // weight. The dictionary is named explicitly so the expression is IMMUTABLE, which Postgres
        // requires of a generated column — the one-argument to_tsvector reads a session setting and is not.
        builder.Property(ticket => ticket.SearchVector)
            .HasColumnType("tsvector")
            .HasComputedColumnSql(
                """
                setweight(to_tsvector('english', coalesce(title, '')), 'A')
                || setweight(to_tsvector('english', coalesce(requester_display_name, '')), 'B')
                || setweight(to_tsvector('english', coalesce(description, '')), 'C')
                """,
                stored: true);
        builder.HasIndex(ticket => ticket.SearchVector).HasMethod("GIN");
        builder.Ignore(ticket => ticket.Number);
    }
}
