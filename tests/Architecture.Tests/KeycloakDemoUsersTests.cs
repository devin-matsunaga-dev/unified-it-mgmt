using System.Text.Json;

namespace Architecture.Tests;

public sealed class KeycloakDemoUsersTests
{
    [Fact]
    public void RealmImport_DemoUsers_MatchesExpectedRoleDistribution()
    {
        var users = HumanUsers();
        var roles = users.Select(user => user.GetProperty("realmRoles")[0].GetString()).ToArray();

        Assert.Equal(20, users.Length);
        Assert.Equal(20, users.Select(user => user.GetProperty("username").GetString())
            .Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(2, roles.Count(role => role == "Admin"));
        Assert.Equal(4, roles.Count(role => role == "Technician"));
        Assert.Equal(4, roles.Count(role => role == "Manager"));
        Assert.Equal(10, roles.Count(role => role == "EndUser"));
    }

    /// <summary>
    /// Each agent's identity: a service account on a client that has no browser flow and no password
    /// grant, so nothing can sign in as it.
    /// </summary>
    [Theory]
    [InlineData("it-platform-poller", "${POLLER_CLIENT_SECRET}")]
    [InlineData("it-platform-discovery", "${DISCOVERY_CLIENT_SECRET}")]
    public void RealmImport_AgentClient_IsAServiceAccountWithNoInteractiveFlow(
        string clientId,
        string secretPlaceholder)
    {
        using var realm = ReadRealm();

        var client = realm.RootElement.GetProperty("clients").EnumerateArray()
            .Single(item => item.GetProperty("clientId").GetString() == clientId);

        Assert.True(client.GetProperty("serviceAccountsEnabled").GetBoolean());
        Assert.False(client.GetProperty("publicClient").GetBoolean());
        Assert.False(client.GetProperty("standardFlowEnabled").GetBoolean());
        Assert.False(client.GetProperty("directAccessGrantsEnabled").GetBoolean());
        // Rendered by AppHost from a generated parameter; a literal here would be a credential in
        // the repository.
        Assert.Equal(secretPlaceholder, client.GetProperty("secret").GetString());
    }

    /// <summary>
    /// The machine roles are for machines: no human user in the realm carries either, and each is held
    /// by exactly one service account.
    /// </summary>
    [Theory]
    [InlineData("Poller", "it-platform-poller")]
    [InlineData("Discovery", "it-platform-discovery")]
    public void RealmImport_AgentRole_IsHeldOnlyByItsOwnServiceAccount(string role, string clientId)
    {
        using var realm = ReadRealm();

        var declared = realm.RootElement.GetProperty("roles").GetProperty("realm").EnumerateArray()
            .Select(item => item.GetProperty("name").GetString()).ToArray();
        Assert.Contains(role, declared);

        Assert.DoesNotContain(
            HumanUsers(),
            user => user.GetProperty("realmRoles").EnumerateArray()
                .Any(item => item.GetString() == role));

        var serviceAccount = realm.RootElement.GetProperty("users").EnumerateArray()
            .Single(user => user.TryGetProperty("serviceAccountClientId", out var owner)
                && owner.GetString() == clientId);
        // Exactly one role each. The scanner deliberately does not also carry Poller: managing what is
        // scanned and redeeming a credential grant are different jobs, and CanPoll reaches the vault.
        Assert.Equal([role], serviceAccount.GetProperty("realmRoles").EnumerateArray()
            .Select(item => item.GetString()));
    }

    /// <summary>
    /// Two service accounts and no more. A third would mean an agent nobody has reviewed the reach of,
    /// and this is the assertion that makes adding one a deliberate act.
    /// </summary>
    [Fact]
    public void RealmImport_ServiceAccounts_AreTheTwoAgentsAndNothingElse()
    {
        using var realm = ReadRealm();

        var accounts = realm.RootElement.GetProperty("users").EnumerateArray()
            .Where(user => user.TryGetProperty("serviceAccountClientId", out _))
            .Select(user => user.GetProperty("serviceAccountClientId").GetString() ?? string.Empty)
            .Order()
            .ToArray();

        Assert.Equal(["it-platform-discovery", "it-platform-poller"], accounts);
    }

    /// <summary>A service account is not a person, so it is excluded from the demo-user counts.</summary>
    private static JsonElement[] HumanUsers()
    {
        using var realm = ReadRealm();
        return [.. realm.RootElement.GetProperty("users").EnumerateArray()
            .Where(user => !user.TryGetProperty("serviceAccountClientId", out _))
            .Select(user => user.Clone())];
    }

    private static JsonDocument ReadRealm() => JsonDocument.Parse(File.ReadAllText(Path.Combine(
        FindRepositoryRoot().FullName,
        "src",
        "AppHost",
        "Keycloak",
        "it-platform-realm.json")));

    private static DirectoryInfo FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ItPlatform.slnx")))
        {
            directory = directory.Parent;
        }

        return directory ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}