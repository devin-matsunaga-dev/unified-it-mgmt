using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Platform.Auditing;
using Platform.Data;
using Platform.Integration;

namespace Platform.Vault;

/// <summary>
/// The credential vault. Reads answer metadata; the one method that answers material is
/// <see cref="RedeemGrantAsync"/>, and it audits every credential it releases.
/// </summary>
public interface ICredentialVault
{
    Task<IReadOnlyList<CredentialResponse>> ListAsync(
        Guid? siteId, CredentialKind? kind, CancellationToken cancellationToken);

    Task<CredentialResponse?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<CredentialResult> CreateAsync(
        CreateCredentialRequest request, ClaimsPrincipal actor, CancellationToken cancellationToken);

    Task<CredentialResult> UpdateAsync(
        Guid id, UpdateCredentialRequest request, ClaimsPrincipal actor, CancellationToken cancellationToken);

    Task<CredentialResult> RotateAsync(
        Guid id, RotateCredentialRequest request, ClaimsPrincipal actor, CancellationToken cancellationToken);

    Task<CredentialResult> DeleteAsync(Guid id, ClaimsPrincipal actor, CancellationToken cancellationToken);

    /// <summary>
    /// The descriptors for a set of ids, in name order, skipping ids that do not exist or are
    /// inactive. Used by Monitoring to tell a poller which credentials it will need and at which
    /// version, without either of them touching the material.
    /// </summary>
    Task<IReadOnlyList<CredentialDescriptor>> DescribeAsync(
        IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);

    /// <summary>
    /// Mints a short-lived, single-use grant over <paramref name="credentialIds"/> for one subject.
    /// The caller is responsible for the scope being one the subject is entitled to — Monitoring
    /// derives it from the poller's own devices rather than from anything the poller asked for.
    /// </summary>
    Task<CredentialGrantResult> IssueGrantAsync(
        string subject,
        string scope,
        IReadOnlyCollection<Guid> credentialIds,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken);

    /// <summary>
    /// Spends a grant and returns the material it covers. The only method in the platform that
    /// decrypts a credential, and the only one that writes an <c>Accessed</c> audit entry.
    /// </summary>
    Task<CredentialRedemptionResult> RedeemGrantAsync(
        RedeemCredentialGrantRequest request, ClaimsPrincipal actor, CancellationToken cancellationToken);
}

public sealed class CredentialVault(
    PlatformDbContext dbContext,
    ICredentialProtector protector,
    IAuditService auditService,
    ICredentialUsageDirectory usageDirectory,
    IOptions<VaultOptions> options,
    ILogger<CredentialVault> logger) : ICredentialVault
{
    private readonly VaultOptions _options = options.Value;

    public async Task<IReadOnlyList<CredentialResponse>> ListAsync(
        Guid? siteId,
        CredentialKind? kind,
        CancellationToken cancellationToken)
    {
        var query = dbContext.Credentials.AsNoTracking().Include(credential => credential.Site);
        var credentials = await query
            .Where(credential => siteId == null || credential.SiteId == siteId)
            .Where(credential => kind == null || credential.Kind == kind)
            .OrderBy(credential => credential.Name)
            .ToListAsync(cancellationToken);
        return [.. credentials.Select(Map)];
    }

    public async Task<CredentialResponse?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var credential = await dbContext.Credentials.AsNoTracking()
            .Include(item => item.Site)
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        return credential is null ? null : Map(credential);
    }

    public async Task<CredentialResult> CreateAsync(
        CreateCredentialRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var material = Normalise(request.Material);
        if (CredentialRules.ValidateMaterial(request.Kind, material) is { Count: > 0 } errors)
        {
            return new(CredentialOutcome.Invalid, Errors: errors);
        }

        var name = request.Name.Trim();
        if (await dbContext.Credentials.AnyAsync(item => item.Name == name, cancellationToken))
        {
            return new(CredentialOutcome.Conflict, Error: $"A credential named '{name}' already exists.");
        }

        if (await SiteProblemAsync(request.SiteId, cancellationToken) is { } siteProblem)
        {
            return new(CredentialOutcome.Invalid, Errors: siteProblem);
        }

        var actorId = ActorId(actor);
        var now = DateTimeOffset.UtcNow;
        var credential = new Credential
        {
            Id = Guid.CreateVersion7(),
            Name = name,
            Kind = request.Kind,
            SiteId = request.SiteId,
            Description = Trim(request.Description),
            SecretCipher = protector.Protect(material),
            Version = 1,
            RotatedAt = now,
            IsActive = request.IsActive,
            CreatedBy = actorId,
            CreatedAt = now,
            UpdatedBy = actorId,
            UpdatedAt = now,
        };
        dbContext.Credentials.Add(credential);
        await dbContext.SaveChangesAsync(cancellationToken);

        // The audit entry carries the response — metadata and the field *names* — and never the
        // entity, whose SecretCipher would put the protected blob into a log every administrator can
        // read. Same rule WP-3.10 applied to a webhook URL.
        var response = await ReloadAsync(credential.Id, cancellationToken);
        await auditService.WriteAsync(
            actor, "Created", "Credential", credential.Id.ToString(), null, response, cancellationToken);
        return new(CredentialOutcome.Success, response);
    }

    public async Task<CredentialResult> UpdateAsync(
        Guid id,
        UpdateCredentialRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var credential = await dbContext.Credentials
            .Include(item => item.Site)
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (credential is null)
        {
            return new(CredentialOutcome.NotFound);
        }

        var name = request.Name.Trim();
        if (await dbContext.Credentials.AnyAsync(
                item => item.Name == name && item.Id != id, cancellationToken))
        {
            return new(CredentialOutcome.Conflict, Error: $"A credential named '{name}' already exists.");
        }

        if (await SiteProblemAsync(request.SiteId, cancellationToken) is { } siteProblem)
        {
            return new(CredentialOutcome.Invalid, Errors: siteProblem);
        }

        var before = Map(credential);
        credential.Name = name;
        credential.SiteId = request.SiteId;
        credential.Description = Trim(request.Description);
        credential.IsActive = request.IsActive;
        credential.UpdatedBy = ActorId(actor);
        credential.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);

        var response = await ReloadAsync(credential.Id, cancellationToken);
        await auditService.WriteAsync(
            actor, "Updated", "Credential", credential.Id.ToString(), before, response, cancellationToken);
        return new(CredentialOutcome.Success, response);
    }

    public async Task<CredentialResult> RotateAsync(
        Guid id,
        RotateCredentialRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var credential = await dbContext.Credentials
            .Include(item => item.Site)
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (credential is null)
        {
            return new(CredentialOutcome.NotFound);
        }

        var material = Normalise(request.Material);
        if (CredentialRules.ValidateMaterial(credential.Kind, material) is { Count: > 0 } errors)
        {
            return new(CredentialOutcome.Invalid, Errors: errors);
        }

        var before = Map(credential);
        credential.SecretCipher = protector.Protect(material);
        // The version is the whole rotation protocol: a poller compares the number it holds against
        // the number the platform reports and asks for material only when they differ.
        credential.Version += 1;
        credential.RotatedAt = DateTimeOffset.UtcNow;
        credential.UpdatedBy = ActorId(actor);
        credential.UpdatedAt = credential.RotatedAt;
        await dbContext.SaveChangesAsync(cancellationToken);

        var response = await ReloadAsync(credential.Id, cancellationToken);
        // "Rotated" rather than "Updated": what changed here is not visible in the before/after pair,
        // because neither of them may carry the secret. The action name is the record of it.
        await auditService.WriteAsync(
            actor, "Rotated", "Credential", credential.Id.ToString(), before, response, cancellationToken);
        return new(CredentialOutcome.Success, response);
    }

    public async Task<CredentialResult> DeleteAsync(
        Guid id,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        var credential = await dbContext.Credentials
            .Include(item => item.Site)
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (credential is null)
        {
            return new(CredentialOutcome.NotFound);
        }

        // Asked through the port, because Platform may not query a module's tables. The same guard
        // WP-2.3 put in front of a CI with relationships: deleting the credential a check names would
        // leave the check pointing at nothing, and it would poll unauthenticated from the next cycle.
        var users = await usageDirectory.CountChecksUsingCredentialAsync(id, cancellationToken);
        if (users > 0)
        {
            return new(
                CredentialOutcome.Conflict,
                Error: $"This credential is used by {users} check(s). Point them elsewhere first, "
                    + "or deactivate the credential instead.");
        }

        var before = Map(credential);
        dbContext.Credentials.Remove(credential);
        await dbContext.SaveChangesAsync(cancellationToken);

        await auditService.WriteAsync(
            actor, "Deleted", "Credential", id.ToString(), before, null, cancellationToken);
        return new(CredentialOutcome.Success);
    }

    public async Task<IReadOnlyList<CredentialDescriptor>> DescribeAsync(
        IReadOnlyCollection<Guid> ids,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0)
        {
            return [];
        }

        var distinct = ids.Distinct().ToList();
        var credentials = await dbContext.Credentials.AsNoTracking()
            .Where(credential => distinct.Contains(credential.Id) && credential.IsActive)
            .OrderBy(credential => credential.Name)
            .ToListAsync(cancellationToken);
        return
        [
            .. credentials.Select(credential =>
                new CredentialDescriptor(credential.Id, credential.Name, credential.Kind, credential.Version)),
        ];
    }

    public async Task<CredentialGrantResult> IssueGrantAsync(
        string subject,
        string scope,
        IReadOnlyCollection<Guid> credentialIds,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentNullException.ThrowIfNull(credentialIds);

        var descriptors = await DescribeAsync(credentialIds, cancellationToken);
        if (descriptors.Count == 0)
        {
            return new(
                CredentialOutcome.Invalid,
                Error: "There is nothing to grant: none of these credentials exists and is active.");
        }

        var now = DateTimeOffset.UtcNow;

        // A grant is a two-minute permission slip, and one is minted whenever a poller notices a
        // rotation or restarts. Sweeping the expired ones on the way past is what keeps the table
        // bounded by the lifetime window instead of growing forever — the retention problem
        // `platform.notification_deliveries` has and nothing solves.
        await dbContext.CredentialGrants
            .Where(grant => grant.ExpiresAt < now)
            .ExecuteDeleteAsync(cancellationToken);

        var token = NewToken();
        var grant = new CredentialGrant
        {
            Id = Guid.CreateVersion7(),
            TokenHash = HashToken(token),
            Subject = subject.Trim(),
            Scope = scope.Trim(),
            IssuedAt = now,
            ExpiresAt = now.AddSeconds(_options.GrantLifetimeSeconds),
            IssuedBy = ActorId(actor),
            Items = [.. descriptors.Select(descriptor =>
                new CredentialGrantItem { CredentialId = descriptor.Id })],
        };
        dbContext.CredentialGrants.Add(grant);
        await dbContext.SaveChangesAsync(cancellationToken);

        // Issuing is audited as well as redeeming. A grant that is minted and never spent is the
        // signature of somebody probing the endpoint, and it leaves no trace in the access trail.
        await auditService.WriteAsync(
            actor,
            "GrantIssued",
            "Credential",
            grant.Id.ToString(),
            null,
            new
            {
                grant.Subject,
                grant.Scope,
                grant.ExpiresAt,
                CredentialIds = descriptors.Select(descriptor => descriptor.Id),
            },
            cancellationToken);

        return new(CredentialOutcome.Success, new CredentialGrantResponse(
            grant.Id, token, grant.ExpiresAt, descriptors));
    }

    public async Task<CredentialRedemptionResult> RedeemGrantAsync(
        RedeemCredentialGrantRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Token))
        {
            return new(CredentialOutcome.NotFound);
        }

        // Looked up by the hash rather than by the id: the token is the secret, and matching on it
        // first means a caller who guesses an id learns nothing. The id is then checked against what
        // the token found, so a token cannot be redeemed against a grant it does not belong to.
        var hash = HashToken(request.Token);
        var grant = await dbContext.CredentialGrants
            .Include(item => item.Items)
            .SingleOrDefaultAsync(item => item.TokenHash == hash, cancellationToken);
        if (grant is null || grant.Id != request.GrantId)
        {
            // One answer for "no such grant" and "wrong token", so neither can be used to enumerate
            // the other.
            return new(CredentialOutcome.NotFound);
        }

        var now = DateTimeOffset.UtcNow;
        if (grant.ExpiresAt <= now)
        {
            return new(CredentialOutcome.Conflict, Error: "This grant has expired. Ask for another.");
        }

        if (grant.RedeemedAt is not null)
        {
            return new(
                CredentialOutcome.Conflict,
                Error: "This grant has already been redeemed. A grant is single-use by design.");
        }

        var credentialIds = grant.Items.Select(item => item.CredentialId).ToList();
        var credentials = await dbContext.Credentials
            .Where(credential => credentialIds.Contains(credential.Id) && credential.IsActive)
            .OrderBy(credential => credential.Name)
            .ToListAsync(cancellationToken);

        var released = new List<ReleasedCredential>(credentials.Count);
        foreach (var credential in credentials)
        {
            var material = protector.Unprotect(credential.SecretCipher);
            if (material is null)
            {
                // A key ring that no longer covers this ciphertext. The id is logged; the ciphertext
                // and the failure's detail are not, because both are hints about the key ring.
                logger.LogError(
                    "Credential {CredentialId} could not be decrypted and was not released. "
                    + "The Data Protection key ring does not cover its ciphertext.",
                    credential.Id);
                continue;
            }

            credential.LastAccessedAt = now;
            released.Add(new ReleasedCredential(
                credential.Id, credential.Name, credential.Kind, credential.Version, material));
        }

        grant.RedeemedAt = now;
        await dbContext.SaveChangesAsync(cancellationToken);

        // ARCHITECTURE §7.3: every access is audited. One entry per credential rather than one per
        // redemption, so "who has read this credential and when" is a query on entity id — which is
        // the question somebody asks after a credential is suspected of having leaked.
        foreach (var credential in released)
        {
            await auditService.WriteAsync(
                actor,
                "Accessed",
                "Credential",
                credential.Id.ToString(),
                null,
                new
                {
                    credential.Name,
                    Kind = credential.Kind.ToString(),
                    credential.Version,
                    GrantId = grant.Id,
                    grant.Subject,
                    grant.Scope,
                },
                cancellationToken);
        }

        return new(CredentialOutcome.Success, new RedeemCredentialGrantResponse(released));
    }

    /// <summary>
    /// 256 bits from the cryptographic RNG, URL-safe so it survives a JSON body and a log line
    /// unescaped. Long enough that guessing is not a strategy against a two-minute window.
    /// </summary>
    private static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    /// <summary>
    /// SHA-256, base64. A plain hash rather than a password KDF on purpose: the input is 256 bits of
    /// machine-generated entropy, so there is no dictionary to slow down, and a grant lives for two
    /// minutes. What this buys is that the stored row is not itself redeemable.
    /// </summary>
    private static string HashToken(string token) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private async Task<CredentialResponse> ReloadAsync(Guid id, CancellationToken cancellationToken)
    {
        var credential = await dbContext.Credentials.AsNoTracking()
            .Include(item => item.Site)
            .SingleAsync(item => item.Id == id, cancellationToken);
        return Map(credential);
    }

    private async Task<IReadOnlyDictionary<string, string[]>?> SiteProblemAsync(
        Guid? siteId,
        CancellationToken cancellationToken)
    {
        if (siteId is not { } id)
        {
            return null;
        }

        return await dbContext.Sites.AnyAsync(site => site.Id == id, cancellationToken)
            ? null
            : new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["SiteId"] = [$"Site '{id}' does not exist."],
            };
    }

    /// <summary>
    /// Field names are trimmed; values are not. A secret's leading or trailing whitespace is part of
    /// the secret, and a vault that quietly trims one is a vault that fails to authenticate for a
    /// reason nobody can see.
    /// </summary>
    private static IReadOnlyDictionary<string, string> Normalise(
        IReadOnlyDictionary<string, string>? material) =>
        material is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : material.ToDictionary(entry => entry.Key.Trim(), entry => entry.Value, StringComparer.Ordinal);

    private CredentialResponse Map(Credential credential) => new(
        credential.Id,
        credential.Name,
        credential.Kind,
        credential.SiteId,
        credential.Site?.Name,
        credential.Description,
        credential.Version,
        // Which fields exist, read out of the ciphertext. An unreadable secret answers with an empty
        // list rather than throwing: the metadata of a credential whose key ring is gone is exactly
        // what somebody needs to see in order to rotate it.
        [.. (protector.Unprotect(credential.SecretCipher)?.Keys ?? []).Order(StringComparer.Ordinal)],
        credential.IsActive,
        credential.RotatedAt,
        credential.LastAccessedAt,
        credential.CreatedBy,
        credential.CreatedAt,
        credential.UpdatedBy,
        credential.UpdatedAt);

    private static string? Trim(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string ActorId(ClaimsPrincipal actor) =>
        actor.FindFirstValue("sub") ?? actor.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException("An authenticated actor identifier is required.");
}
