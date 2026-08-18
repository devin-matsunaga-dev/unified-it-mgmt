using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Platform.Data;

public sealed class DepartmentSiteConfiguration : IEntityTypeConfiguration<DepartmentSite>
{
    public void Configure(EntityTypeBuilder<DepartmentSite> builder)
    {
        builder.ToTable("department_sites");
        builder.HasKey(link => new { link.DepartmentId, link.SiteId });
        builder.HasOne(link => link.Department).WithMany(department => department.Sites)
            .HasForeignKey(link => link.DepartmentId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(link => link.Site).WithMany(site => site.Departments)
            .HasForeignKey(link => link.SiteId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(link => link.SiteId);
    }
}
