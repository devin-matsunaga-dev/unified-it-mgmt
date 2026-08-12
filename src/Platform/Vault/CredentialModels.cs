using Platform.Data;

namespace Platform.Vault;

/// <summary>
/// Everything a read of a credential is allowed to answer with. There is deliberately no member here
/// that could hold secret material, and there never should be — the type is the guard.
/// </summary>
/// <param name="Fields">
/// Which fields the secret has, so a form can say "this one carries a privKey". Never their values.
/// </param>
public sealed record CredentialResponse(
    Guid Id,
    string Name,
    CredentialKind Kind,
    Guid? SiteId,
    string? SiteName,
    string? Description,
    int Version,
    IReadOnlyList<string> Fields,
    bool IsActive,
    DateTimeOffset RotatedAt,
    DateTimeOffset? LastAccessedAt,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    string UpdatedBy,
    DateTimeOffset UpdatedAt);

public sealed record CreateCredentialRequest(
    string Name,
    CredentialKind Kind,
    IReadOnlyDictionary<string, string> Material,
    Guid? SiteId = null,
    string? Description = null,
    bool IsActive = true);

/// <summary>
/// A rotation replaces the whole secret. There is no partial form on purpose: a payload that could
/// change the auth key while keeping the priv key would need to read the stored one to merge them,
/// and the point of this vault is that nothing reads a stored secret except a redemption.
/// </summary>
public sealed record RotateCredentialRequest(IReadOnlyDictionary<string, string> Material);

/// <summary>
/// The metadata an operator can change without touching the secret. The <see cref="CredentialKind"/>
/// is absent because it decides the shape of the material already stored.
/// </summary>
public sealed record UpdateCredentialRequest(
    string Name,
    Guid? SiteId = null,
    string? Description = null,
    bool IsActive = true);

/// <summary>What a poller is told about a credential before it asks for the secret.</summary>
public sealed record CredentialDescriptor(Guid Id, string Name, CredentialKind Kind, int Version);

/// <summary>
/// A minted grant. <see cref="Token"/> is the only time the token exists outside the holder — the row
/// keeps a hash — so a caller that loses it mints another rather than recovering this one.
/// </summary>
public sealed record CredentialGrantResponse(
    Guid GrantId,
    string Token,
    DateTimeOffset ExpiresAt,
    IReadOnlyList<CredentialDescriptor> Credentials);

public sealed record RedeemCredentialGrantRequest(Guid GrantId, string Token);

/// <summary>
/// One credential's material, on its way out of the platform to the thing that will authenticate with
/// it. This is the <em>only</em> type in the repository that carries plaintext secret fields, and the
/// only place it is constructed is <see cref="ICredentialVault.RedeemGrantAsync"/>.
/// </summary>
public sealed record ReleasedCredential(
    Guid Id,
    string Name,
    CredentialKind Kind,
    int Version,
    IReadOnlyDictionary<string, string> Material);

public sealed record RedeemCredentialGrantResponse(IReadOnlyList<ReleasedCredential> Credentials);

public enum CredentialOutcome
{
    Success,
    NotFound,
    Invalid,
    Conflict,
}

public sealed record CredentialResult(
    CredentialOutcome Outcome,
    CredentialResponse? Credential = null,
    IReadOnlyDictionary<string, string[]>? Errors = null,
    string? Error = null);

public sealed record CredentialGrantResult(
    CredentialOutcome Outcome,
    CredentialGrantResponse? Grant = null,
    string? Error = null);

public sealed record CredentialRedemptionResult(
    CredentialOutcome Outcome,
    RedeemCredentialGrantResponse? Released = null,
    string? Error = null);
