using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Modules.Assets.Data;
using Modules.Assets.Features.BulkEdit;
using Modules.Assets.Features.Cis;
using Modules.Assets.Features.Contracts;
using Modules.Assets.Features.Discovery;
using Modules.Assets.Features.Drift;
using Modules.Assets.Features.Import;
using Modules.Assets.Features.Labels;
using Modules.Assets.Features.Lifecycle;
using Modules.Assets.Features.PhysicalAudits;
using Modules.Assets.Features.Relationships;
using Modules.Assets.Features.Software;
using Modules.Assets.Features.Topology;
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
        services.AddScoped<ICiDependencyDirectory, CiDependencyDirectory>();
        services.AddScoped<ICiImportService, CiImportService>();
        services.AddScoped<ICiBulkEditService, CiBulkEditService>();
        services.AddScoped<ICiLabelService, CiLabelService>();
        services.AddScoped<IVendorService, VendorService>();
        services.AddScoped<IContractService, ContractService>();
        services.AddScoped<IContractExpiryService, ContractExpiryService>();
        services.AddScoped<IDiscoveryReviewService, DiscoveryReviewService>();
        services.AddScoped<IDriftService, DriftService>();
        services.AddScoped<IPhysicalAuditService, PhysicalAuditService>();
        services.AddScoped<ITopologyService, TopologyService>();
        services.AddScoped<ITopologyMapService, TopologyMapService>();
        services.AddScoped<ISoftwareCatalogService, SoftwareCatalogService>();
        services.AddScoped<ISoftwareImportService, SoftwareImportService>();
        services.AddScoped<ILicensingService, LicensingService>();
        services.AddScoped<ISoftwareComplianceService, SoftwareComplianceService>();
        services.AddQuartz(quartz =>
        {
            // Daily, starting at host start-up: the pass is idempotent, so an extra run costs nothing
            // and a renewal notice is visible without waiting for tomorrow.
            var jobKey = new JobKey("contract-expiry");
            quartz.AddJob<ContractExpiryJob>(builder => builder.WithIdentity(jobKey));
            quartz.AddTrigger(builder => builder.ForJob(jobKey).WithIdentity("contract-expiry-daily")
                .StartNow().WithSimpleSchedule(schedule => schedule.WithIntervalInHours(24).RepeatForever()));

            // The same shape for licence over-deployment (WP-4.4). It is a separate job rather than a
            // second half of the expiry pass because it answers a different question: not "what runs
            // out" but "what is installed more widely than it was bought for".
            var complianceKey = new JobKey("software-compliance");
            quartz.AddJob<SoftwareComplianceJob>(builder => builder.WithIdentity(complianceKey));
            quartz.AddTrigger(builder => builder.ForJob(complianceKey).WithIdentity("software-compliance-daily")
                .StartNow().WithSimpleSchedule(schedule => schedule.WithIntervalInHours(24).RepeatForever()));
        });

        return services;
    }

    /// <summary>
    /// Assets' first consumer. Handed to Platform's single <c>AddMassTransit</c> through the same
    /// callback Helpdesk and Monitoring use — the bus is configured once per host, while a consumer
    /// belongs to the module that reacts (WP-3.2).
    /// </summary>
    public static void AddAssetsConsumers(IBusRegistrationConfigurator bus)
    {
        ArgumentNullException.ThrowIfNull(bus);
        bus.AddConsumer<DeviceDiscoveredConsumer>();
    }
}
