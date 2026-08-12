using Microsoft.EntityFrameworkCore;

using Platform.Data;
using Platform.Vault;

namespace Platform.Seeding;

/// <param name="CredentialIds">
/// The seeded credentials by key, for the monitoring seeder to hang its SNMP checks off. Ids travel as
/// an argument rather than being looked up, the same route WP-2.8's ticket↔CI links and WP-3.3's
/// monitored devices take.
/// </param>
public sealed record CredentialSeedResult(
    int CredentialsAdded,
    IReadOnlyDictionary<string, Guid> CredentialIds);

/// <summary>
/// The two SNMP communities the seeded estate polls with, in the vault rather than in the clear.
/// <para>
/// This exists so that a fresh <c>aspire run</c> exercises the vault end to end — a check with a
/// credential id, a poller that redeems a grant for it, and an audit trail of the access — rather than
/// leaving the whole feature to a fixture somebody makes by hand against a database that is recreated
/// on the next restart. Before WP-3.11 both of these strings sat in <c>check_definitions.parameters</c>
/// as plain jsonb, readable by anybody who could read a check.
/// </para>
/// <para>
/// The values are the simulator's profile names and are not secret in any real sense; what is being
/// demonstrated is the path, not the confidentiality of "healthy".
/// </para>
/// </summary>
public sealed class CredentialSeeder(PlatformDbContext dbContext, ICredentialProtector protector)
{
    public const string HealthyKey = "snmp-healthy";
    public const string DegradedKey = "snmp-degraded";

    private const string SeedActor = "system:seeder";

    public async Task<CredentialSeedResult> SeedAsync(
        string healthyCommunity = "healthy",
        string degradedCommunity = "degraded",
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var added = 0;
        var ids = new Dictionary<string, Guid>(StringComparer.Ordinal);

        foreach (var (key, id, name, community, description) in new[]
        {
            (HealthyKey, Id(1), "Simulator SNMP — healthy profile", healthyCommunity,
                "Read-only community the seeded switch polls with. The simulator serves a quiet device profile through it."),
            (DegradedKey, Id(2), "Simulator SNMP — degraded profile", degradedCommunity,
                "Read-only community the seeded server polls with. The simulator serves a device under strain through it."),
        })
        {
            ids[key] = id;
            // Idempotent by id, like every other seeder here. A re-run must not re-encrypt an existing
            // credential: that would bump nothing but would replace a secret an operator may have
            // rotated by hand, which is exactly the fixture they were testing with.
            if (await dbContext.Credentials.AnyAsync(item => item.Id == id, cancellationToken))
            {
                continue;
            }

            dbContext.Credentials.Add(new Credential
            {
                Id = id,
                Name = name,
                Kind = CredentialKind.SnmpV2c,
                SiteId = null,
                Description = description,
                SecretCipher = protector.Protect(
                    new Dictionary<string, string>(StringComparer.Ordinal) { ["community"] = community }),
                Version = 1,
                RotatedAt = now,
                IsActive = true,
                CreatedBy = SeedActor,
                CreatedAt = now,
                UpdatedBy = SeedActor,
                UpdatedAt = now,
            });
            added++;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return new CredentialSeedResult(added, ids);
    }

    /// <summary>Fixed ids so a re-run is idempotent and the monitoring seeder can name them.</summary>
    private static Guid Id(int index) =>
        Guid.Parse($"0199c0de-3110-7000-8000-0000000000{index:d2}");
}
