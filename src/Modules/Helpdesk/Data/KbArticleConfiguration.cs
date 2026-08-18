using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Modules.Helpdesk.Data;

public sealed class KbArticleConfiguration : IEntityTypeConfiguration<KbArticle>
{
    public void Configure(EntityTypeBuilder<KbArticle> builder)
    {
        builder.ToTable("kb_articles", "helpdesk");
        builder.HasKey(article => article.Id);

        // KB-000012 the same way a ticket is INC-000012 and a problem PRB-000012, from the database rather
        // than from a count: two people writing an article at once must not both be told they wrote the first.
        builder.Property(article => article.SequenceNumber).UseIdentityAlwaysColumn();
        builder.HasIndex(article => article.SequenceNumber).IsUnique();

        builder.Property(article => article.Title).HasMaxLength(200).IsRequired();
        builder.Property(article => article.Summary).HasMaxLength(500).IsRequired();
        builder.Property(article => article.Body).HasMaxLength(50_000).IsRequired();
        builder.Property(article => article.Keywords).HasMaxLength(500);
        builder.Property(article => article.Status).HasConversion<string>().HasMaxLength(16);
        builder.Property(article => article.AuthorId).HasMaxLength(200).IsRequired();
        builder.Property(article => article.AuthorName).HasMaxLength(200).IsRequired();
        builder.Property(article => article.UpdatedById).HasMaxLength(200).IsRequired();
        builder.Property(article => article.UpdatedByName).HasMaxLength(200).IsRequired();
        builder.Property(article => article.PublishedById).HasMaxLength(200);
        builder.Property(article => article.PublishedByName).HasMaxLength(200);

        builder.HasOne(article => article.Category).WithMany()
            .HasForeignKey(article => article.CategoryId)
            .OnDelete(DeleteBehavior.Restrict);

        // SetNull rather than Restrict: the article outlives the problem that prompted it, and losing the
        // provenance is a smaller loss than a problem nobody can delete.
        builder.HasOne(article => article.Problem).WithMany()
            .HasForeignKey(article => article.ProblemId)
            .OnDelete(DeleteBehavior.SetNull);

        // The same weighted, generated column the ticket carries (WP-5.4), for the same two reasons: setweight
        // is what makes ts_rank prefer a title match to a passing mention, and writing the SQL out is the only
        // way to weight — HasGeneratedTsVectorColumn cannot. The dictionary is named explicitly so the
        // expression is IMMUTABLE, which Postgres requires of a generated column.
        //
        // Keywords sit at B beside the summary because they exist for exactly the search the prose fails:
        // somebody typing the name everybody uses for a thing the manual calls something else.
        builder.Property(article => article.SearchVector)
            .HasColumnType("tsvector")
            .HasComputedColumnSql(
                """
                setweight(to_tsvector('english', coalesce(title, '')), 'A')
                || setweight(to_tsvector('english', coalesce(summary, '')), 'B')
                || setweight(to_tsvector('english', coalesce(keywords, '')), 'B')
                || setweight(to_tsvector('english', coalesce(body, '')), 'C')
                """,
                stored: true);
        builder.HasIndex(article => article.SearchVector).HasMethod("GIN");

        // The reads this table gets: the browse list filtered by state, the category page, and the
        // suggestion query — which is always narrowed to published before it is ranked.
        builder.HasIndex(article => article.Status);
        builder.HasIndex(article => article.CategoryId);
        builder.Ignore(article => article.Number);
    }
}

public sealed class KbArticleRevisionConfiguration : IEntityTypeConfiguration<KbArticleRevision>
{
    public void Configure(EntityTypeBuilder<KbArticleRevision> builder)
    {
        builder.ToTable("kb_article_revisions", "helpdesk");
        builder.HasKey(revision => revision.Id);

        builder.HasOne(revision => revision.Article).WithMany(article => article.Revisions)
            .HasForeignKey(revision => revision.ArticleId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Property(revision => revision.Title).HasMaxLength(200).IsRequired();
        builder.Property(revision => revision.Summary).HasMaxLength(500).IsRequired();
        builder.Property(revision => revision.Body).HasMaxLength(50_000).IsRequired();
        builder.Property(revision => revision.Keywords).HasMaxLength(500);
        builder.Property(revision => revision.AuthorId).HasMaxLength(200).IsRequired();
        builder.Property(revision => revision.AuthorName).HasMaxLength(200).IsRequired();

        // One row per version per article, made true by the database: two edits racing must leave one of
        // them with a failed insert rather than with two histories that both claim to be version 4.
        builder.HasIndex(revision => new { revision.ArticleId, revision.Version }).IsUnique();
    }
}

public sealed class TicketKbArticleConfiguration : IEntityTypeConfiguration<TicketKbArticle>
{
    public void Configure(EntityTypeBuilder<TicketKbArticle> builder)
    {
        builder.ToTable("ticket_kb_articles", "helpdesk");
        builder.HasKey(link => link.Id);

        // Deleting a ticket takes its article links with it, exactly as it takes its CI links and its
        // problem link: the link is a statement about a ticket and cannot outlive one.
        builder.HasOne(link => link.Ticket).WithMany()
            .HasForeignKey(link => link.TicketId)
            .OnDelete(DeleteBehavior.Cascade);

        // Restrict, unlike the ticket side. An article attached to a resolved ticket is part of that
        // ticket's record — archiving is how an article goes away, and the service says so in the refusal.
        builder.HasOne(link => link.Article).WithMany()
            .HasForeignKey(link => link.ArticleId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(link => new { link.TicketId, link.ArticleId }).IsUnique();
        builder.HasIndex(link => link.ArticleId);

        builder.Property(link => link.LinkedById).HasMaxLength(200).IsRequired();
        builder.Property(link => link.LinkedByName).HasMaxLength(200).IsRequired();
    }
}
