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

                // WP-3.9. A browser cannot set an Authorization header on a WebSocket handshake, so
                // the SignalR client puts the token in the query string instead. Accepted for the hub
                // paths only — a token in a URL is a token in a log line and an access log full of
                // them would be a credential leak on every other endpoint.
                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        var token = context.Request.Query["access_token"];
                        if (!string.IsNullOrEmpty(token)
                            && context.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                        {
                            context.Token = token;
                        }

                        return Task.CompletedTask;
                    },
                };
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
                PlatformRoles.Manager))
            // WP-5.6. Narrower than CanManageMonitoring on purpose, and the omission is the decision:
            // a Manager may configure what is watched but may not restart a service on it, because
            // running a runbook is an action on a machine rather than a change to a configuration.
            // EndUser is nowhere near it, and the Poller and Discovery service accounts are excluded
            // for the reason ARCHITECTURE §6 gives — an agent must not hold an operator's rights.
            .AddPolicy(AuthorizationPolicies.CanRunRunbooks, policy => policy.RequireRole(
                PlatformRoles.Admin,
                PlatformRoles.Technician))
            // The poller's own credential, and nothing else — not even Admin. A poller reads its
            // configuration with the identity it polls under, and the endpoints behind this policy
            // are the only ones it can reach; an operator inspecting pollers uses GET /api/pollers,
            // which stays on CanManageMonitoring.
            .AddPolicy(AuthorizationPolicies.CanPoll, policy => policy.RequireRole(PlatformRoles.Poller))
            // The discovery service's credential, and nothing else — not Admin, and deliberately not
            // Poller either. A scanner reads scan profiles; it has no devices to configure and no
            // credential scope to redeem, so the two service identities stay disjoint and a stolen
            // scanner token buys nothing the vault protects.
            .AddPolicy(AuthorizationPolicies.CanDiscover,
                policy => policy.RequireRole(PlatformRoles.Discovery));

        return services;
    }
}
