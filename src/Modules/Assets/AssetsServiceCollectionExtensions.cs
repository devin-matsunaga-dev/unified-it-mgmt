using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Modules.Assets.Data;
using Modules.Assets.Features.Cis;
using Modules.Assets.Features.Lifecycle;

namespace Modules.Assets;

public static class AssetsServiceCollectionExtensions
{
    public static IServiceCollection AddAssetsServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddDbContext<AssetsDbContext>((provider, options) =>
        {
            var connectionString = provider.GetRequiredService<IConfiguration>().GetConnectionString("database")
                ?? throw new InvalidOperationException("Connection string 'database' is required.");
            options.UseNpgsql(connectionString);
        });
        services.AddScoped<ICiService, CiService>();
        services.AddScoped<ICiLifecycleService, CiLifecycleService>();

        return services;
    }
}
