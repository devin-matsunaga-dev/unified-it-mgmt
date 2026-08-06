using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Modules.Helpdesk.Data;

public sealed class TicketCommentConfiguration : IEntityTypeConfiguration<TicketComment>
{
    public void Configure(EntityTypeBuilder<TicketComment> builder)
    {
        builder.HasKey(comment => comment.Id);
        builder.Property(comment => comment.Body).HasMaxLength(10_000).IsRequired();
        builder.Property(comment => comment.AuthorId).HasMaxLength(200).IsRequired();
        builder.HasOne(comment => comment.Ticket).WithMany().HasForeignKey(comment => comment.TicketId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(comment => new { comment.TicketId, comment.CreatedAt });
    }
}

public sealed class TicketWorklogConfiguration : IEntityTypeConfiguration<TicketWorklog>
{
    public void Configure(EntityTypeBuilder<TicketWorklog> builder)
    {
        builder.HasKey(worklog => worklog.Id);
        builder.Property(worklog => worklog.Note).HasMaxLength(2_000);
        builder.Property(worklog => worklog.AuthorId).HasMaxLength(200).IsRequired();
        builder.HasOne(worklog => worklog.Ticket).WithMany().HasForeignKey(worklog => worklog.TicketId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(worklog => new { worklog.TicketId, worklog.CreatedAt });
    }
}

public sealed class TicketAttachmentConfiguration : IEntityTypeConfiguration<TicketAttachment>
{
    public void Configure(EntityTypeBuilder<TicketAttachment> builder)
    {
        builder.HasKey(attachment => attachment.Id);
        builder.Property(attachment => attachment.FileName).HasMaxLength(255).IsRequired();
        builder.Property(attachment => attachment.ContentType).HasMaxLength(200).IsRequired();
        builder.Property(attachment => attachment.ObjectKey).HasMaxLength(500).IsRequired();
        builder.Property(attachment => attachment.UploadedById).HasMaxLength(200).IsRequired();
        builder.HasIndex(attachment => attachment.ObjectKey).IsUnique();
        builder.HasOne(attachment => attachment.Ticket).WithMany().HasForeignKey(attachment => attachment.TicketId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(attachment => new { attachment.TicketId, attachment.CreatedAt });
    }
}
