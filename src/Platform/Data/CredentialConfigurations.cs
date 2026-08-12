using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Platform.Data;

public sealed class CredentialConfiguration : IEntityTypeConfiguration<Credential>
{
    public void Configure(EntityTypeBuilder<Credential> builder)
    {
        builder.ToTable("credentials", "platform");
        builder.HasKey(credential => credential.Id);
        builder.Property(credential => credential.Name).HasMaxLength(200).IsRequired();
        builder.Property(credential => credential.Kind).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(credential => credential.Description).HasMaxLength(1_000);
        // No length limit: the ciphertext of an SSH private key is several kilobytes, and a truncated
        // secret is one that decrypts to nothing at the moment somebody needs it.
        builder.Property(credential => credential.SecretCipher).IsRequired();
        builder.Property(credential => credential.CreatedBy).HasMaxLength(200).IsRequired();
        builder.Property(credential => credential.UpdatedBy).HasMaxLength(200).IsRequired();

        // The name is what an operator picks on a check, so two credentials cannot share one.
        builder.HasIndex(credential => credential.Name).IsUnique();
        builder.HasIndex(credential => new { credential.SiteId, credential.IsActive });

        // Restrict, not cascade: a site is deleted by an administrator tidying the directory, and
        // taking the site's credentials with it would silently unauthenticate every check using them.
        builder.HasOne(credential => credential.Site).WithMany()
            .HasForeignKey(credential => credential.SiteId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class CredentialGrantConfiguration : IEntityTypeConfiguration<CredentialGrant>
{
    public void Configure(EntityTypeBuilder<CredentialGrant> builder)
    {
        builder.ToTable("credential_grants", "platform");
        builder.HasKey(grant => grant.Id);
        builder.Property(grant => grant.TokenHash).HasMaxLength(64).IsRequired();
        builder.Property(grant => grant.Subject).HasMaxLength(100).IsRequired();
        builder.Property(grant => grant.Scope).HasMaxLength(100).IsRequired();
        builder.Property(grant => grant.IssuedBy).HasMaxLength(200).IsRequired();

        // Redemption looks a grant up by its hash and nothing else, so the token is the only thing a
        // holder needs and the id in the request is checked against it rather than trusted.
        builder.HasIndex(grant => grant.TokenHash).IsUnique();
        // Sweeping expired grants is a range scan on this, run on every issue.
        builder.HasIndex(grant => grant.ExpiresAt);
    }
}

public sealed class CredentialGrantItemConfiguration : IEntityTypeConfiguration<CredentialGrantItem>
{
    public void Configure(EntityTypeBuilder<CredentialGrantItem> builder)
    {
        builder.ToTable("credential_grant_items", "platform");
        builder.HasKey(item => new { item.GrantId, item.CredentialId });

        builder.HasOne(item => item.Grant).WithMany(grant => grant.Items)
            .HasForeignKey(item => item.GrantId)
            .OnDelete(DeleteBehavior.Cascade);

        // A credential deleted while a grant names it takes the grant's row with it: the grant is a
        // two-minute permission slip, not a record anybody reads afterwards.
        builder.HasOne(item => item.Credential).WithMany()
            .HasForeignKey(item => item.CredentialId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class DataProtectionKeyConfiguration : IEntityTypeConfiguration<DataProtectionKey>
{
    public void Configure(EntityTypeBuilder<DataProtectionKey> builder)
    {
        builder.ToTable("data_protection_keys", "platform");
        builder.HasKey(key => key.Id);
        builder.Property(key => key.FriendlyName).HasMaxLength(200);
        builder.Property(key => key.Xml).IsRequired();
    }
}
