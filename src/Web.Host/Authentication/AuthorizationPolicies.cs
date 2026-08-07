namespace Web.Host.Authentication;

public static class AuthorizationPolicies
{
    public const string AdminOnly = "AdminOnly";
    public const string CanManageTickets = "CanManageTickets";
    public const string CanManageAssets = "CanManageAssets";
}

public static class PlatformRoles
{
    public const string Admin = "Admin";
    public const string Technician = "Technician";
    public const string Manager = "Manager";
    public const string EndUser = "EndUser";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        Admin,
        Technician,
        Manager,
        EndUser,
    };
}
