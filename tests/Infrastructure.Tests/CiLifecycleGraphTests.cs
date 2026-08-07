using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Modules.Assets.Data;

namespace Infrastructure.Tests;

/// <summary>
/// The lifecycle guard is seeded data, so these assertions read it out of the model rather than a
/// database: the graph must stay a chain with returns, and must never contain a shortcut.
/// </summary>
public sealed class CiLifecycleGraphTests
{
    private static readonly (CiLifecycleState From, CiLifecycleState To)[] Graph = SeededGraph();

    [Theory]
    [InlineData(CiLifecycleState.Ordered, CiLifecycleState.InStock)]
    [InlineData(CiLifecycleState.InStock, CiLifecycleState.Deployed)]
    [InlineData(CiLifecycleState.Deployed, CiLifecycleState.InRepair)]
    [InlineData(CiLifecycleState.InRepair, CiLifecycleState.Deployed)]
    [InlineData(CiLifecycleState.Deployed, CiLifecycleState.Retired)]
    [InlineData(CiLifecycleState.Retired, CiLifecycleState.Disposed)]
    public void SeededGraph_AllowsTheWorkingLifecycle(CiLifecycleState from, CiLifecycleState to) =>
        Assert.Contains((from, to), Graph);

    [Theory]
    [InlineData(CiLifecycleState.Ordered, CiLifecycleState.Disposed)]
    [InlineData(CiLifecycleState.Ordered, CiLifecycleState.Deployed)]
    [InlineData(CiLifecycleState.InStock, CiLifecycleState.Disposed)]
    [InlineData(CiLifecycleState.Deployed, CiLifecycleState.Disposed)]
    [InlineData(CiLifecycleState.Disposed, CiLifecycleState.InStock)]
    public void SeededGraph_RejectsSkippingStates(CiLifecycleState from, CiLifecycleState to) =>
        Assert.DoesNotContain((from, to), Graph);

    [Fact]
    public void SeededGraph_HasNoSelfTransitions() =>
        Assert.DoesNotContain(Graph, edge => edge.From == edge.To);

    [Fact]
    public void SeededGraph_LeavesDisposedTerminal() =>
        Assert.DoesNotContain(Graph, edge => edge.From == CiLifecycleState.Disposed);

    /// <summary>Every state except the terminal one must be reachable, or a CI could strand.</summary>
    [Fact]
    public void SeededGraph_ReachesEveryStateFromOrdered()
    {
        var reached = new HashSet<CiLifecycleState> { CiLifecycleState.Ordered };
        while (Graph.Any(edge => reached.Contains(edge.From) && reached.Add(edge.To)))
        {
        }

        Assert.Equal(Enum.GetValues<CiLifecycleState>().ToHashSet(), reached);
    }

    private static (CiLifecycleState From, CiLifecycleState To)[] SeededGraph()
    {
        var options = new DbContextOptionsBuilder<AssetsDbContext>()
            .UseNpgsql("Host=localhost;Database=model-only")
            .Options;
        using var context = new AssetsDbContext(options);
        // Seed data lives on the design-time model; the runtime model drops it.
        var entityType = context.GetService<IDesignTimeModel>().Model
            .FindEntityType(typeof(CiLifecycleTransition))!;
        return
        [
            .. entityType.GetSeedData().Select(row => (
                (CiLifecycleState)row[nameof(CiLifecycleTransition.FromState)]!,
                (CiLifecycleState)row[nameof(CiLifecycleTransition.ToState)]!))
        ];
    }
}
