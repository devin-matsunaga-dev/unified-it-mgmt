namespace Modules.Assets.Features.Cis;

/// <summary>
/// What a network device does, which <see cref="Data.CiType.NetworkDevice"/> alone cannot say — a
/// core switch and a desk-side access switch are the same CI type and belong at opposite ends of a
/// topology.
/// <para>
/// Recorded rather than guessed. The topology previously had no way to tell these apart and the only
/// alternatives were inferring from the CI's name, which is prose, or from its depth in the
/// dependency graph, which is wrong the moment a relationship is missing.
/// </para>
/// <para>
/// The order here is the order the estate is drawn in, edge first, and the order a form offers.
/// </para>
/// </summary>
public static class NetworkDeviceRoles
{
    public const string Edge = "Edge";
    public const string Firewall = "Firewall";
    public const string Core = "Core";
    public const string Distribution = "Distribution";
    public const string Access = "Access";
    public const string Wireless = "Wireless";

    public static readonly IReadOnlyList<string> All = [Edge, Firewall, Core, Distribution, Access, Wireless];

    /// <summary>Where a role sits in the hierarchy; lower is nearer the edge. Unset sorts last.</summary>
    public static int Rank(string? role) => role switch
    {
        Edge => 0,
        Firewall => 1,
        Core => 2,
        Distribution => 3,
        Access => 4,
        Wireless => 5,
        _ => int.MaxValue,
    };
}
