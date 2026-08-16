using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Platform.Data;

public sealed class UserProfileConfiguration : IEntityTypeConfiguration<UserProfile>
{
    private static readonly string[] Roles = ["Admin", "Technician", "Manager", "EndUser"];

    public void Configure(EntityTypeBuilder<UserProfile> builder)
    {
        builder.ToTable("user_profiles", table => table.HasCheckConstraint(
            "ck_user_profiles_role",
            $"role IN ({string.Join(", ", Roles.Select(role => $"'{role}'"))})"));
        builder.HasKey(user => user.Id);
        builder.Property(user => user.Id).ValueGeneratedNever();
        builder.Property(user => user.Username).HasMaxLength(100).IsRequired();
        builder.Property(user => user.Email).HasMaxLength(320).IsRequired();
        builder.Property(user => user.DisplayName).HasMaxLength(200).IsRequired();
        builder.Property(user => user.Role).HasMaxLength(50).IsRequired();
        builder.HasIndex(user => user.Username).IsUnique();
        builder.HasIndex(user => user.Email).IsUnique();
        builder.HasOne(user => user.Site).WithMany().HasForeignKey(user => user.SiteId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(user => user.Department).WithMany().HasForeignKey(user => user.DepartmentId)
            .OnDelete(DeleteBehavior.Restrict);

        // WP-5.4. Display name first because that is what somebody types; username and email are here so
        // that "enduser3" and "enduser3@example.test" both find the person, which is how an operator
        // arrives from a ticket's requester id rather than from a name.
        builder.HasGeneratedTsVectorColumn(
                user => user.SearchVector,
                "english",
                user => new { user.DisplayName, user.Username, user.Email })
            .HasIndex(user => user.SearchVector).HasMethod("GIN");
    }
}