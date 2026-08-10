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
    /// The poller's identity: a service account with the Poller role, on a client that has no
    /// browser flow and no password grant, so nothing can sign in as it.
    /// </summary>
    [Fact]
    public void RealmImport_PollerClient_IsAServiceAccountWithNoInteractiveFlow()
    {
        using var realm = ReadRealm();

        var client = realm.RootElement.GetProperty("clients").EnumerateArray()
            .Single(item => item.GetProperty("clientId").GetString() == "it-platform-poller");

        Assert.True(client.GetProperty("serviceAccountsEnabled").GetBoolean());
        Assert.False(client.GetProperty("publicClient").GetBoolean());
        Assert.False(client.GetProperty("standardFlowEnabled").GetBoolean());
        Assert.False(client.GetProperty("directAccessGrantsEnabled").GetBoolean());
        // Rendered by AppHost from a generated parameter; a literal here would be a credential in
        // the repository.
        Assert.Equal("${POLLER_CLIENT_SECRET}", client.GetProperty("secret").GetString());
    }

    /// <summary>The Poller role is for machines: no human user in the realm carries it.</summary>
    [Fact]
    public void RealmImport_PollerRole_IsHeldOnlyByTheServiceAccount()
    {
        using var realm = ReadRealm();

        var declared = realm.RootElement.GetProperty("roles").GetProperty("realm").EnumerateArray()
            .Select(role => role.GetProperty("name").GetString()).ToArray();
        Assert.Contains("Poller", declared);

        Assert.DoesNotContain(
            HumanUsers(),
            user => user.GetProperty("realmRoles").EnumerateArray()
                .Any(role => role.GetString() == "Poller"));

        var serviceAccount = realm.RootElement.GetProperty("users").EnumerateArray()
            .Single(user => user.TryGetProperty("serviceAccountClientId", out _));
        Assert.Equal("it-platform-poller", serviceAccount.GetProperty("serviceAccountClientId").GetString());
        Assert.Equal(["Poller"], serviceAccount.GetProperty("realmRoles").EnumerateArray()
            .Select(role => role.GetString()));
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