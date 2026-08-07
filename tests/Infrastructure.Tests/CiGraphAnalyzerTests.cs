using Modules.Assets.Data;
using Modules.Assets.Features.Relationships;

namespace Infrastructure.Tests;

/// <summary>
/// Cycle reporting is pure graph reasoning over an edge set, so it is exercised without a database.
/// The traversal itself never loops — these assertions are about what it tells the caller afterwards.
/// </summary>
public sealed class CiGraphAnalyzerTests
{
    [Fact]
    public void ContainsCycle_LinearChain_IsFalse()
    {
        var vm = Guid.CreateVersion7();
        var host = Guid.CreateVersion7();
        var switchCi = Guid.CreateVersion7();
        var router = Guid.CreateVersion7();

        Assert.False(CiGraphAnalyzer.ContainsCycle(
            [Edge(vm, host), Edge(host, switchCi), Edge(switchCi, router)]));
    }

    [Fact]
    public void ContainsCycle_NoEdges_IsFalse() => Assert.False(CiGraphAnalyzer.ContainsCycle([]));

    /// <summary>A diamond revisits a node without ever revisiting it on the same path.</summary>
    [Fact]
    public void ContainsCycle_DiamondWithSharedDependency_IsFalse()
    {
        var app = Guid.CreateVersion7();
        var left = Guid.CreateVersion7();
        var right = Guid.CreateVersion7();
        var storage = Guid.CreateVersion7();

        Assert.False(CiGraphAnalyzer.ContainsCycle(
            [Edge(app, left), Edge(app, right), Edge(left, storage), Edge(right, storage)]));
    }

    [Fact]
    public void ContainsCycle_TwoNodesPointingAtEachOther_IsTrue()
    {
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();

        Assert.True(CiGraphAnalyzer.ContainsCycle([Edge(first, second), Edge(second, first)]));
    }

    [Fact]
    public void ContainsCycle_LongLoop_IsTrue()
    {
        var a = Guid.CreateVersion7();
        var b = Guid.CreateVersion7();
        var c = Guid.CreateVersion7();

        Assert.True(CiGraphAnalyzer.ContainsCycle([Edge(a, b), Edge(b, c), Edge(c, a)]));
    }

    /// <summary>A loop reachable only from a separate root must still be found.</summary>
    [Fact]
    public void ContainsCycle_LoopHangingOffAChain_IsTrue()
    {
        var root = Guid.CreateVersion7();
        var a = Guid.CreateVersion7();
        var b = Guid.CreateVersion7();

        Assert.True(CiGraphAnalyzer.ContainsCycle([Edge(root, a), Edge(a, b), Edge(b, a)]));
    }

    /// <summary>A deep chain must not overflow the stack the way a recursive walk would.</summary>
    [Fact]
    public void ContainsCycle_VeryDeepChain_IsFalse()
    {
        var ids = Enumerable.Range(0, 5_000).Select(_ => Guid.CreateVersion7()).ToArray();
        var edges = ids.Zip(ids.Skip(1), Edge).ToArray();

        Assert.False(CiGraphAnalyzer.ContainsCycle(edges));
    }

    private static CiGraphEdge Edge(Guid source, Guid target) =>
        new(Guid.CreateVersion7(), source, target, CiRelationshipType.DependsOn);
}
