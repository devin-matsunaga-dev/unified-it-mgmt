namespace Platform.Data;

/// <summary>
/// What a credential is for. The kind decides which fields the secret carries and which check types
/// may use it — an SNMP v2c community cannot stand in for an SSH key — so it is fixed at creation.
/// Rotating a credential replaces its secret; changing what the secret <em>is</em> is a new credential.
/// </summary>
public enum CredentialKind
{
    /// <summary>A community string. One field, and the whole of SNMP v2c's security model.</summary>
    SnmpV2c,

    /// <summary>USM: a security name plus optional authentication and privacy keys.</summary>
    SnmpV3,

    /// <summary>A username with a password or a private key.</summary>
    Ssh,

    /// <summary>A Windows account: username, password and an optional domain.</summary>
    Wmi,
}

/// <summary>
/// A secret the platform holds on behalf of something that has to authenticate to a device.
/// <para>
/// ARCHITECTURE §7.3 in one sentence: the material is <b>write-only</b>. It is encrypted at rest by
/// ASP.NET Data Protection before it reaches this row, no API ever returns it, and the only path that
/// decrypts it — redeeming a <see cref="CredentialGrant"/> — writes an audit entry naming who read
/// what. Everything else in the system handles the <see cref="Id"/> and the metadata beside it.
/// </para>
/// <para>
/// A credential is scoped to a <see cref="SiteId"/> or to nothing. A site-scoped credential is the
/// common case — the read-only community the network team uses at Head Office — and a null scope is
/// the estate-wide one. The scope is metadata rather than an enforcement point today: nothing in
/// WP-3.11 refuses a check whose device sits at another site, because a monitored device carries no
/// site of its own (the CI does, and Monitoring reads that live through a port).
/// </para>
/// </summary>
public sealed class Credential
{
    public Guid Id { get; set; }

    /// <summary>Unique platform-wide. It is how an operator picks one on a check, so it has to be a name.</summary>
    public required string Name { get; set; }

    public CredentialKind Kind { get; set; }

    /// <summary>The site this credential belongs to, or null for one that applies estate-wide.</summary>
    public Guid? SiteId { get; set; }

    public Site? Site { get; set; }

    public string? Description { get; set; }

    /// <summary>
    /// The protected secret: a JSON object of the kind's fields, run through
    /// <see cref="Platform.Vault.ICredentialProtector"/>. Never returned by anything, never logged, and
    /// never put in an audit entry — see <see cref="Platform.Vault.CredentialVault"/>, where every read
    /// of this column is one method.
    /// </summary>
    public required string SecretCipher { get; set; }

    /// <summary>
    /// Bumped by every rotation, starting at 1. This is what a poller compares against the version it
    /// holds, which is the whole mechanism behind "rotate the credential and the poller picks it up
    /// next cycle": the material itself never travels unless the number moved.
    /// </summary>
    public int Version { get; set; }

    /// <summary>When the secret was last replaced. Equal to <see cref="CreatedAt"/> until it is.</summary>
    public DateTimeOffset RotatedAt { get; set; }

    /// <summary>
    /// When material was last released to anybody. Metadata rather than the audit trail — the trail is
    /// in <c>platform.audit_entries</c> — but it is the field that answers "is this still in use?"
    /// without reading a log.
    /// </summary>
    public DateTimeOffset? LastAccessedAt { get; set; }

    /// <summary>An inactive credential is never released, and a check that names one polls unauthenticated.</summary>
    public bool IsActive { get; set; } = true;

    public required string CreatedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public required string UpdatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// A short-lived, single-use permission to read a named set of credentials.
/// <para>
/// The grant exists so that holding the poller's bearer token is not by itself enough to drain the
/// vault. A grant is minted for one poller, over the exact credentials that poller's own devices
/// reference, and it expires in a couple of minutes whether or not it is used. The token is returned
/// once and stored only as a SHA-256 hash, the same way a password would be: a stolen database gives
/// somebody rows they cannot redeem.
/// </para>
/// </summary>
public sealed class CredentialGrant
{
    public Guid Id { get; set; }

    /// <summary>SHA-256 of the issued token, base64. The token itself is never stored anywhere.</summary>
    public required string TokenHash { get; set; }

    /// <summary>The poller this grant was issued to. A grant is not transferable between pollers.</summary>
    public required string Subject { get; set; }

    /// <summary>The poller group whose devices decided the scope. Recorded so the audit entry can say it.</summary>
    public required string Scope { get; set; }

    public DateTimeOffset IssuedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>Set by the one redemption this grant allows. A second attempt is a 409.</summary>
    public DateTimeOffset? RedeemedAt { get; set; }

    public required string IssuedBy { get; set; }

    public ICollection<CredentialGrantItem> Items { get; set; } = [];
}

/// <summary>One credential a grant covers. The scope is a list of ids and nothing else.</summary>
public sealed class CredentialGrantItem
{
    public Guid GrantId { get; set; }

    public CredentialGrant Grant { get; set; } = null!;

    public Guid CredentialId { get; set; }

    public Credential Credential { get; set; } = null!;
}

/// <summary>
/// One ASP.NET Data Protection key, kept in Postgres rather than on the container's filesystem.
/// <para>
/// This is load-bearing and easy to overlook: Data Protection defaults to a key ring under the app's
/// own directory, so a container that restarts mints a fresh key and <em>every credential in the vault
/// becomes undecryptable</em>. Persisting the ring beside the ciphertext means the two travel together
/// through a restart, a redeploy and a database restore.
/// </para>
/// <para>
/// The ring itself is stored unencrypted, which is the honest state of this in dev: on a single host
/// the key and the ciphertext are equally readable to anybody with the database. Protecting the ring
/// at rest wants a KMS, an HSM or a certificate that is not in this repository, and that is a
/// deployment decision rather than a code one — see DECISIONS.md.
/// </para>
/// </summary>
public sealed class DataProtectionKey
{
    public int Id { get; set; }

    public string? FriendlyName { get; set; }

    public required string Xml { get; set; }
}
