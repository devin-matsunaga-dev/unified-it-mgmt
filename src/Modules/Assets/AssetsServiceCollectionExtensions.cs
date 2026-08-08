using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Modules.Assets.Data;
using Modules.Assets.Features.BulkEdit;
using Modules.Assets.Features.Cis;
using Modules.Assets.Features.Contracts;
using Modules.Assets.Features.Import;
using Modules.Assets.Features.Lifecycle;
using Modules.Assets.Features.Relationships;
using Platform.Integration;
using Quartz;

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
        services.AddScoped<IVendorService, VendorService>();
        services.AddScoped<IContractService, ContractService>();
        services.AddScoped<IContractExpiryService, ContractExpiryService>();
        services.AddQuartz(quartz =>
        {
            // Daily, starting at host start-up: the pass is idempotent, so an extra run costs nothing
            // and a renewal notice is visible without waiting for tomorrow.
            var jobKey = new JobKey("contract-expiry");
            quartz.AddJob<ContractExpiryJob>(builder => builder.WithIdentity(jobKey));
            quartz.AddTrigger(builder => builder.ForJob(jobKey).WithIdentity("contract-expiry-daily")
                .StartNow().WithSimpleSchedule(schedule => schedule.WithIntervalInHours(24).RepeatForever()));
        });

        return services;
    }
}
