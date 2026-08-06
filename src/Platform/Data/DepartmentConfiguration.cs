using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Platform.Data;

public sealed class DepartmentConfiguration : IEntityTypeConfiguration<Department>
{
    public void Configure(EntityTypeBuilder<Department> builder)
    {
        builder.ToTable("departments", table =>
            table.HasCheckConstraint("ck_departments_code_not_empty", "length(code) > 0"));
        builder.HasKey(department => department.Id);
        builder.Property(department => department.Id).ValueGeneratedNever();
        builder.Property(department => department.Code).HasMaxLength(50).IsRequired();
        builder.Property(department => department.Name).HasMaxLength(200).IsRequired();
        builder.HasIndex(department => department.Code).IsUnique();
    }
}