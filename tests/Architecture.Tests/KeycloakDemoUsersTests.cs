using System.Text.Json;

namespace Architecture.Tests;

public sealed class KeycloakDemoUsersTests
{
    [Fact]
    public void RealmImport_DemoUsers_MatchesExpectedRoleDistribution()
    {
        var repositoryRoot = FindRepositoryRoot();
        using var realm = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            repositoryRoot.FullName,
            "src",
            "AppHost",
            "Keycloak",
            "it-platform-realm.json")));
        var users = realm.RootElement.GetProperty("users").EnumerateArray().ToArray();
        var roles = users.Select(user => user.GetProperty("realmRoles")[0].GetString()).ToArray();

        Assert.Equal(20, users.Length);
        Assert.Equal(20, users.Select(user => user.GetProperty("username").GetString())
            .Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(2, roles.Count(role => role == "Admin"));
        Assert.Equal(4, roles.Count(role => role == "Technician"));
        Assert.Equal(4, roles.Count(role => role == "Manager"));
        Assert.Equal(10, roles.Count(role => role == "EndUser"));
    }

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