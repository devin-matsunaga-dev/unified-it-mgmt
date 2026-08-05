namespace Architecture.Tests;

public sealed class SolutionStructureTests
{
    private static readonly string[] ExpectedProjects =
    [
        "src/AppHost/AppHost.csproj",
        "src/Contracts/Contracts.csproj",
        "src/Modules/Assets/Modules.Assets.csproj",
        "src/Modules/Helpdesk/Modules.Helpdesk.csproj",
        "src/Modules/Monitoring/Modules.Monitoring.csproj",
        "src/Platform/Platform.csproj",
        "src/Web.Host/Web.Host.csproj",
        "tests/Architecture.Tests/Architecture.Tests.csproj",
    ];

    [Fact]
    public void Solution_ExpectedProjects_ArePresentAndTargetNet10()
    {
        var repositoryRoot = FindRepositoryRoot();
        var solution = File.ReadAllText(Path.Combine(repositoryRoot.FullName, "ItPlatform.slnx"));

        foreach (var project in ExpectedProjects)
        {
            Assert.Contains(project, solution, StringComparison.Ordinal);

            var projectContents = File.ReadAllText(Path.Combine(repositoryRoot.FullName, project));
            Assert.Contains("<TargetFramework>net10.0</TargetFramework>", projectContents, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void SolutionProjects_DuplicateProjectPath_IsRejected()
    {
        var duplicateProjects = ExpectedProjects.Append(ExpectedProjects[0]);

        Assert.Throws<InvalidOperationException>(() => EnsureUnique(duplicateProjects));
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

    private static void EnsureUnique(IEnumerable<string> projectPaths)
    {
        var paths = projectPaths.ToArray();

        if (paths.Distinct(StringComparer.Ordinal).Count() != paths.Length)
        {
            throw new InvalidOperationException("Solution project paths must be unique.");
        }
    }
}
