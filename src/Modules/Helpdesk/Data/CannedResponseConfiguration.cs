using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Modules.Helpdesk.Data;

public sealed class CannedResponseConfiguration : IEntityTypeConfiguration<CannedResponse>
{
    public void Configure(EntityTypeBuilder<CannedResponse> builder)
    {
        builder.ToTable("canned_responses", "helpdesk");
        builder.HasKey(response => response.Id);
        builder.Property(response => response.Name).HasMaxLength(100).IsRequired();
        builder.Property(response => response.Body).HasMaxLength(10_000).IsRequired();
        builder.Property(response => response.CreatedById).HasMaxLength(200).IsRequired();
        builder.HasIndex(response => response.Name).IsUnique();
    }
}
