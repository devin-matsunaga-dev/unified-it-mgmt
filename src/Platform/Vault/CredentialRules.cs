using Platform.Data;

namespace Platform.Vault;

/// <summary>
/// What makes a credential's secret coherent for its kind, and which fields of it are secret at all.
/// <para>
/// Pure and infrastructure-free, the same shape as Monitoring's <c>CheckRules</c>: the whole matrix is
/// unit-testable without a database, a protector or an HTTP request, which matters more here than
/// anywhere else in the repo because these rules are the last thing between an operator's typo and a
/// check that authenticates with an empty key.
/// </para>
/// </summary>
public static class CredentialRules
{
    public const int MaximumFields = 12;
    public const int MaximumFieldNameLength = 50;
    public const int MaximumFieldValueLength = 8_000;

    /// <summary>
    /// Every field each kind understands. A field outside its kind's list is refused rather than
    /// stored, because a secret nothing reads is a secret somebody believes is in force.
    /// </summary>
    public static IReadOnlyDictionary<CredentialKind, IReadOnlyList<string>> Fields { get; } =
        new Dictionary<CredentialKind, IReadOnlyList<string>>
        {
            [CredentialKind.SnmpV2c] = ["community"],
            [CredentialKind.SnmpV3] =
                ["securityName", "authProtocol", "authKey", "privProtocol", "privKey"],
            [CredentialKind.Ssh] = ["username", "password", "privateKey", "passphrase"],
            [CredentialKind.Wmi] = ["username", "password", "domain"],
        };

    /// <summary>The fields that must be present and non-blank.</summary>
    public static IReadOnlyDictionary<CredentialKind, IReadOnlyList<string>> RequiredFields { get; } =
        new Dictionary<CredentialKind, IReadOnlyList<string>>
        {
            [CredentialKind.SnmpV2c] = ["community"],
            [CredentialKind.SnmpV3] = ["securityName"],
            [CredentialKind.Ssh] = ["username"],
            [CredentialKind.Wmi] = ["username", "password"],
        };

    /// <summary>
    /// SNMP v3's USM protocol names, spelled without hyphens or case so "SHA-256", "sha256" and
    /// "Sha256" are one answer. These mirror <c>services/poller/src/poller/checks/snmp.py</c> by hand,
    /// which is the same cross-language duplication <c>AlertRules.PrimaryMetric</c> already carries —
    /// if that file's tuples change, this list has to change with it and nothing but a failing check
    /// will say so.
    /// </summary>
    public static IReadOnlyList<string> AuthProtocols { get; } =
        ["none", "md5", "sha", "sha224", "sha256", "sha384", "sha512"];

    public static IReadOnlyList<string> PrivProtocols { get; } =
        ["none", "des", "3des", "aes", "aes192", "aes256"];

    /// <summary>
    /// Which credential kinds a monitoring check type can authenticate with, keyed by the check type's
    /// name so Platform does not need Monitoring's enum. A type absent from here takes no credential —
    /// ICMP has nothing to authenticate to, and TCP and TLS answer without identifying themselves.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<CredentialKind>> KindsByCheckType { get; } =
        new Dictionary<string, IReadOnlyList<CredentialKind>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Snmp"] = [CredentialKind.SnmpV2c, CredentialKind.SnmpV3],
        };

    /// <summary>
    /// The first thing wrong with a secret, keyed by field, or an empty dictionary if there is nothing.
    /// </summary>
    public static IReadOnlyDictionary<string, string[]> ValidateMaterial(
        CredentialKind kind,
        IReadOnlyDictionary<string, string> material)
    {
        ArgumentNullException.ThrowIfNull(material);
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var known = Fields[kind];

        if (material.Count > MaximumFields)
        {
            errors["Material"] = [$"A credential carries at most {MaximumFields} fields."];
            return errors;
        }

        foreach (var (key, value) in material)
        {
            if (string.IsNullOrWhiteSpace(key) || key.Length > MaximumFieldNameLength)
            {
                errors["Material"] = [$"Field names must be 1 to {MaximumFieldNameLength} characters."];
                return errors;
            }

            if (value.Length > MaximumFieldValueLength)
            {
                // Named without its value, obviously. Every message this class produces is one that
                // may end up in a log or a browser's network tab.
                errors["Material"] = [$"Field '{key}' is longer than {MaximumFieldValueLength} characters."];
                return errors;
            }

            if (!known.Contains(key, StringComparer.Ordinal))
            {
                errors["Material"] =
                    [$"A {kind} credential has no field '{key}'. It carries {string.Join(", ", known)}."];
                return errors;
            }
        }

        foreach (var required in RequiredFields[kind])
        {
            if (!material.TryGetValue(required, out var supplied) || string.IsNullOrWhiteSpace(supplied))
            {
                errors["Material"] = [$"A {kind} credential requires a '{required}' field."];
                return errors;
            }
        }

        if (kind is CredentialKind.SnmpV3 && SnmpV3Problem(material) is { } problem)
        {
            errors["Material"] = [problem];
        }

        if (kind is CredentialKind.Ssh
            && Supplied(material, "password") is null && Supplied(material, "privateKey") is null)
        {
            errors["Material"] = ["An Ssh credential requires either a 'password' or a 'privateKey'."];
        }

        return errors;
    }

    /// <summary>
    /// USM's three security levels are noAuthNoPriv, authNoPriv and authPriv. The fourth combination —
    /// privacy without authentication — does not exist, and a device offered it refuses the request in
    /// a way that reads as a dead agent. This mirrors the check the poller makes on the same fields.
    /// </summary>
    private static string? SnmpV3Problem(IReadOnlyDictionary<string, string> material)
    {
        var auth = Protocol(material, "authProtocol");
        var priv = Protocol(material, "privProtocol");

        if (!AuthProtocols.Contains(auth, StringComparer.Ordinal))
        {
            return $"'authProtocol' must be one of {string.Join(", ", AuthProtocols)}.";
        }

        if (!PrivProtocols.Contains(priv, StringComparer.Ordinal))
        {
            return $"'privProtocol' must be one of {string.Join(", ", PrivProtocols)}.";
        }

        if (auth != "none" && Supplied(material, "authKey") is null)
        {
            return $"SNMP v3 auth protocol '{auth}' needs an 'authKey'.";
        }

        if (priv != "none" && Supplied(material, "privKey") is null)
        {
            return $"SNMP v3 priv protocol '{priv}' needs a 'privKey'.";
        }

        if (priv != "none" && auth == "none")
        {
            return "SNMP v3 privacy requires authentication; set 'authProtocol'.";
        }

        return null;
    }

    /// <summary>True when a check of this type may name a credential of this kind.</summary>
    public static bool Accepts(string checkType, CredentialKind kind) =>
        KindsByCheckType.TryGetValue(checkType, out var kinds) && kinds.Contains(kind);

    /// <summary>The kinds a check type accepts, empty for one that authenticates to nothing.</summary>
    public static IReadOnlyList<CredentialKind> AcceptedKinds(string checkType) =>
        KindsByCheckType.TryGetValue(checkType, out var kinds) ? kinds : [];

    private static string Protocol(IReadOnlyDictionary<string, string> material, string name) =>
        (Supplied(material, name) ?? "none").ToLowerInvariant().Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal);

    /// <summary>A field an operator actually set. A blank one is unset, not an empty secret.</summary>
    private static string? Supplied(IReadOnlyDictionary<string, string> material, string name) =>
        material.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;
}
