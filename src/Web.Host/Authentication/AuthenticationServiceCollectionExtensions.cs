using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;

namespace Web.Host.Authentication;

public static class AuthenticationServiceCollectionExtensions
{
    public static IServiceCollection AddPlatformAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<AuthenticationOptions>()
            .Bind(configuration.GetSection(AuthenticationOptions.SectionName))
            .Validate(options => Uri.TryCreate(options.Authority, UriKind.Absolute, out _),
                "Authentication:Authority must be an absolute URI.")
            .Validate(options => !string.IsNullOrWhiteSpace(options.Audience),
                "Authentication:Audience is required.")
            .Validate(options => !string.IsNullOrWhiteSpace(options.ClientId),
                "Authentication:ClientId is required.")
            .Validate(options => Uri.TryCreate(options.PostLogoutRedirectUri, UriKind.Absolute, out _),
                "Authentication:PostLogoutRedirectUri must be an absolute URI.")
            .ValidateOnStart();

        var authentication = configuration
            .GetRequiredSection(AuthenticationOptions.SectionName)
            .Get<AuthenticationOptions>() ?? throw new InvalidOperationException("Authentication configuration is required.");

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.Authority = authentication.Authority;
                options.Audience = authentication.Audience;
                options.RequireHttpsMetadata = authentication.RequireHttpsMetadata;
            });
        services.AddTransient<IClaimsTransformation, KeycloakRoleClaimsTransformation>();
        services.AddAuthorizationBuilder()
            .AddPolicy(AuthorizationPolicies.AdminOnly, policy => policy.RequireRole(PlatformRoles.Admin))
            .AddPolicy(AuthorizationPolicies.CanManageTickets, policy => policy.RequireRole(
                PlatformRoles.Admin,
                PlatformRoles.Technician,
                PlatformRoles.Manager,
                PlatformRoles.EndUser))
            // Deliberately excludes EndUser: the CMDB is an agent surface, and CanManageTickets
            // includes EndUser so that requesters can reach the portal.
            .AddPolicy(AuthorizationPolicies.CanManageAssets, policy => policy.RequireRole(
                PlatformRoles.Admin,
                PlatformRoles.Technician,
                PlatformRoles.Manager))
            // Same agent-only shape as the CMDB. Additive: it exists so the monitoring surface can
            // diverge from Assets later without editing a policy two modules already depend on.
            .AddPolicy(AuthorizationPolicies.CanManageMonitoring, policy => policy.RequireRole(
                PlatformRoles.Admin,
                PlatformRoles.Technician,
                PlatformRoles.Manager));

        return services;
    }
}
