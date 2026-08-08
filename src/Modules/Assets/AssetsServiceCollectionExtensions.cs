using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Modules.Assets.Data;
using Modules.Assets.Features.BulkEdit;
using Modules.Assets.Features.Cis;
using Modules.Assets.Features.Import;
using Modules.Assets.Features.Lifecycle;
using Modules.Assets.Features.Relationships;
using Platform.Integration;

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
        services.AddScoped<ICiDirectory, CiDirectory>();
        services.AddScoped<ICiLifecycleService, CiLifecycleService>();
        services.AddScoped<ICiRelationshipService, CiRelationshipService>();
        services.AddScoped<ICiImportService, CiImportService>();
        services.AddScoped<ICiBulkEditService, CiBulkEditService>();

        return services;
    }
}
