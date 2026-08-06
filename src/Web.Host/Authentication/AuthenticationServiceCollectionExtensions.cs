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
            .AddPolicy(AuthorizationPolicies.AdminOnly, policy => policy.RequireRole(PlatformRoles.Admin));

        return services;
    }
}
