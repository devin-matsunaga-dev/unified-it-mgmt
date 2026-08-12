using System.Text.Json;

using Microsoft.AspNetCore.DataProtection;

namespace Platform.Vault;

/// <summary>
/// Turns a credential's fields into the ciphertext stored on the row, and back again.
/// <para>
/// An interface with exactly two methods, so that every line in this repository that can turn a
/// credential into plaintext is reachable from here — which is what makes "the material is
/// write-only" something a reviewer can check rather than a claim.
/// </para>
/// </summary>
public interface ICredentialProtector
{
    string Protect(IReadOnlyDictionary<string, string> material);

    /// <summary>
    /// The stored secret, or null when the ciphertext cannot be read — a key ring that has been lost
    /// or replaced. Null rather than an exception because a credential nobody can decrypt must degrade
    /// to "this check polls unauthenticated and the delivery is recorded as failed", not to a 500 on
    /// the poller's own configuration path.
    /// </summary>
    IReadOnlyDictionary<string, string>? Unprotect(string cipher);
}

public sealed class CredentialProtector : ICredentialProtector
{
    /// <summary>
    /// The Data Protection purpose. It is versioned in its own name because a purpose string is part
    /// of the key derivation: changing it makes every existing ciphertext unreadable, so a future
    /// format change gets <c>.v2</c> and a migration rather than an edit to this line.
    /// </summary>
    public const string Purpose = "Platform.Vault.CredentialMaterial.v1";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly IDataProtector _protector;

    public CredentialProtector(IDataProtectionProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _protector = provider.CreateProtector(Purpose);
    }

    public string Protect(IReadOnlyDictionary<string, string> material)
    {
        ArgumentNullException.ThrowIfNull(material);
        return _protector.Protect(JsonSerializer.Serialize(material, SerializerOptions));
    }

    public IReadOnlyDictionary<string, string>? Unprotect(string cipher)
    {
        if (string.IsNullOrWhiteSpace(cipher))
        {
            return null;
        }

        try
        {
            var json = _protector.Unprotect(cipher);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json, SerializerOptions);
        }
        catch (Exception)
        {
            // Deliberately caught without the exception in the message anywhere: a cryptographic
            // failure's detail is a hint about the key ring, and the caller only ever needs to know
            // that this credential is unreadable. The caller logs the credential's id, not this.
            return null;
        }
    }
}
