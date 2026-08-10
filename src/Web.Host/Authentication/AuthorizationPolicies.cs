namespace Web.Host.Authentication;

public static class AuthorizationPolicies
{
    public const string AdminOnly = "AdminOnly";
    public const string CanManageTickets = "CanManageTickets";
    public const string CanManageAssets = "CanManageAssets";
    public const string CanManageMonitoring = "CanManageMonitoring";

    /// <summary>
    /// A polling agent reading its own configuration. Deliberately disjoint from every operator
    /// policy: a poller must not need an agent's rights, and an agent has no business registering
    /// one.
    /// </summary>
    public const string CanPoll = "CanPoll";
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

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        Admin,
        Technician,
        Manager,
        EndUser,
        Poller,
    };
}
