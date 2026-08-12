using Platform.Data;
using Platform.Vault;

namespace Infrastructure.Tests;

/// <summary>
/// The vault's validation matrix, with no infrastructure — the same shape as
/// <see cref="CheckRulesTests"/>. These rules are the last thing between an operator's typo and a
/// check that authenticates with nothing, so they are worth testing exhaustively and cheaply.
/// </summary>
public sealed class CredentialRulesTests
{
    [Fact]
    public void Validate_ASnmpV2cCommunity_IsAccepted()
    {
        var errors = CredentialRules.ValidateMaterial(
            CredentialKind.SnmpV2c, Material(("community", "public")));

        Assert.Empty(errors);
    }

    [Theory]
    [InlineData(CredentialKind.SnmpV2c, "community")]
    [InlineData(CredentialKind.SnmpV3, "securityName")]
    [InlineData(CredentialKind.Ssh, "username")]
    [InlineData(CredentialKind.Wmi, "username")]
    public void Validate_WithoutTheKindsRequiredField_IsRefused(CredentialKind kind, string missing)
    {
        var errors = CredentialRules.ValidateMaterial(kind, Material());

        Assert.Contains(missing, Assert.Single(errors).Value[0], StringComparison.Ordinal);
    }

    /// <summary>A blank value is an unset field, not an empty secret.</summary>
    [Fact]
    public void Validate_WithABlankRequiredField_IsRefused()
    {
        var errors = CredentialRules.ValidateMaterial(
            CredentialKind.SnmpV2c, Material(("community", "   ")));

        Assert.NotEmpty(errors);
    }

    /// <summary>
    /// A field the kind does not understand is refused rather than stored, because a secret nothing
    /// reads is a secret somebody believes is in force.
    /// </summary>
    [Fact]
    public void Validate_WithAFieldTheKindDoesNotHave_IsRefused()
    {
        var errors = CredentialRules.ValidateMaterial(
            CredentialKind.SnmpV2c, Material(("community", "public"), ("privateKey", "-----BEGIN")));

        Assert.Contains("privateKey", Assert.Single(errors).Value[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// USM has three security levels; privacy without authentication is the combination that does
    /// not exist, and a device offered it refuses in a way that reads as a dead agent.
    /// </summary>
    [Fact]
    public void Validate_SnmpV3WithPrivacyButNoAuthentication_IsRefused()
    {
        var errors = CredentialRules.ValidateMaterial(CredentialKind.SnmpV3, Material(
            ("securityName", "monitor"), ("privProtocol", "aes"), ("privKey", "secret")));

        Assert.Contains("authentication", Assert.Single(errors).Value[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_SnmpV3WithAnAuthProtocolAndNoKey_IsRefused()
    {
        var errors = CredentialRules.ValidateMaterial(CredentialKind.SnmpV3, Material(
            ("securityName", "monitor"), ("authProtocol", "sha256")));

        Assert.Contains("authKey", Assert.Single(errors).Value[0], StringComparison.Ordinal);
    }

    /// <summary>"SHA-256", "sha256" and "Sha256" are one answer, matching the poller's own spelling.</summary>
    [Theory]
    [InlineData("SHA-256")]
    [InlineData("sha256")]
    [InlineData("Sha_256")]
    public void Validate_SnmpV3AuthProtocol_IgnoresCaseAndSeparators(string spelling)
    {
        var errors = CredentialRules.ValidateMaterial(CredentialKind.SnmpV3, Material(
            ("securityName", "monitor"), ("authProtocol", spelling), ("authKey", "secret")));

        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_SnmpV3WithAProtocolNobodyImplements_IsRefused()
    {
        var errors = CredentialRules.ValidateMaterial(CredentialKind.SnmpV3, Material(
            ("securityName", "monitor"), ("authProtocol", "rot13"), ("authKey", "secret")));

        Assert.Contains("authProtocol", Assert.Single(errors).Value[0], StringComparison.Ordinal);
    }

    /// <summary>SNMP v3 with no auth and no priv is noAuthNoPriv, which is a real security level.</summary>
    [Fact]
    public void Validate_SnmpV3WithNeitherAuthNorPrivacy_IsAccepted()
    {
        var errors = CredentialRules.ValidateMaterial(
            CredentialKind.SnmpV3, Material(("securityName", "monitor")));

        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_SshWithNeitherPasswordNorKey_IsRefused()
    {
        var errors = CredentialRules.ValidateMaterial(
            CredentialKind.Ssh, Material(("username", "monitor")));

        Assert.Contains("privateKey", Assert.Single(errors).Value[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_SshWithAPrivateKeyAndNoPassword_IsAccepted()
    {
        var errors = CredentialRules.ValidateMaterial(CredentialKind.Ssh, Material(
            ("username", "monitor"), ("privateKey", "-----BEGIN OPENSSH PRIVATE KEY-----")));

        Assert.Empty(errors);
    }

    /// <summary>
    /// Every message this class produces can end up in a log or a browser's network tab, so a
    /// complaint about a field names the field and never quotes the value.
    /// </summary>
    [Fact]
    public void Validate_WhenAValueIsTooLong_NamesTheFieldWithoutQuotingIt()
    {
        var secret = new string('s', CredentialRules.MaximumFieldValueLength + 1);

        var errors = CredentialRules.ValidateMaterial(
            CredentialKind.SnmpV2c, Material(("community", secret)));

        var message = Assert.Single(errors).Value[0];
        Assert.Contains("community", message, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Snmp", CredentialKind.SnmpV2c, true)]
    [InlineData("Snmp", CredentialKind.SnmpV3, true)]
    [InlineData("Snmp", CredentialKind.Ssh, false)]
    [InlineData("Icmp", CredentialKind.SnmpV2c, false)]
    [InlineData("Tcp", CredentialKind.SnmpV2c, false)]
    [InlineData("Http", CredentialKind.SnmpV2c, false)]
    [InlineData("Tls", CredentialKind.SnmpV2c, false)]
    public void Accepts_MatchesTheCheckTypeToTheKindsItCanAuthenticateWith(
        string checkType,
        CredentialKind kind,
        bool expected)
    {
        Assert.Equal(expected, CredentialRules.Accepts(checkType, kind));
    }

    /// <summary>
    /// The kind names every field it declares, and every required field is one of them — a required
    /// field missing from the declared list would be impossible to supply.
    /// </summary>
    [Theory]
    [InlineData(CredentialKind.SnmpV2c)]
    [InlineData(CredentialKind.SnmpV3)]
    [InlineData(CredentialKind.Ssh)]
    [InlineData(CredentialKind.Wmi)]
    public void RequiredFields_AreAlwaysFieldsTheKindDeclares(CredentialKind kind)
    {
        Assert.All(
            CredentialRules.RequiredFields[kind],
            required => Assert.Contains(required, CredentialRules.Fields[kind]));
    }

    private static Dictionary<string, string> Material(params (string Key, string Value)[] fields) =>
        fields.ToDictionary(field => field.Key, field => field.Value, StringComparer.Ordinal);
}
