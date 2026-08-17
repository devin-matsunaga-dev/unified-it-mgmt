namespace Web.Host.Authentication;

public static class AuthorizationPolicies
{
    public const string AdminOnly = "AdminOnly";
    public const string CanManageTickets = "CanManageTickets";
    public const string CanManageAssets = "CanManageAssets";
    public const string CanManageMonitoring = "CanManageMonitoring";

    /// <summary>
    /// Running an auto-remediation runbook, and reading what has been run (WP-5.6).
    /// <para>
    /// Its own policy rather than <see cref="CanManageMonitoring"/>, because executing something on a
    /// machine is a different act from configuring what is watched — and narrower than it: a Manager
    /// may edit a check but may not restart a service. Administering the allowlist itself is narrower
    /// still and sits on <see cref="AdminOnly"/>.
    /// </para>
    /// <para>
    /// Disjoint from <see cref="CanPoll"/> in both directions, which ARCHITECTURE §6 requires: an
    /// operator cannot collect an execution off the agent channel, and an agent cannot ask for one.
    /// </para>
    /// </summary>
    public const string CanRunRunbooks = "CanRunRunbooks";

    /// <summary>
    /// A polling agent reading its own configuration. Deliberately disjoint from every operator
    /// policy: a poller must not need an agent's rights, and an agent has no business registering
    /// one.
    /// </summary>
    public const string CanPoll = "CanPoll";

    /// <summary>
    /// A discovery service reading the ranges it is meant to scan. Disjoint from every operator policy
    /// for the same reason <see cref="CanPoll"/> is, and disjoint from <see cref="CanPoll"/> as well: a
    /// scanner has no devices and no credential scope, so nothing it does should reach the vault.
    /// </summary>
    public const string CanDiscover = "CanDiscover";
}

public static class PlatformRoles
{
    public const string Admin = "Admin";
    public const string Technician = "Technician";
    public const string Manager = "Manager";
    public const string EndUser = "EndUser";

    /// <summary>
    /// Held by service accounts only. It grants nothing a person needs and is never in the realm
    /// roles of a human user — see the realm import.
    /// </summary>
    public const string Poller = "Poller";

    /// <summary>
    /// The discovery service's own role, on the same terms as <see cref="Poller"/>: machines only, and
    /// never in a human user's realm roles. Separate from Poller so that a compromised scanner cannot
    /// redeem a credential grant — the two agents do different work and reach different endpoints.
    /// </summary>
    public const string Discovery = "Discovery";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        Admin,
        Technician,
        Manager,
        EndUser,
        Poller,
        Discovery,
    };
}
