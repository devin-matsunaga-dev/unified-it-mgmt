using MassTransit;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Modules.Monitoring.Data;
using Modules.Monitoring.Features.Devices;
using Modules.Monitoring.Features.Heartbeats;
using Modules.Monitoring.Features.MaintenanceWindows;
using Modules.Monitoring.Features.PollerConfig;
using Quartz;

namespace Modules.Monitoring;

public static class MonitoringServiceCollectionExtensions
{
    public static IServiceCollection AddMonitoringServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddDbContext<MonitoringDbContext>((provider, options) =>
        {
            var connectionString = provider.GetRequiredService<IConfiguration>().GetConnectionString("database")
                ?? throw new InvalidOperationException("Connection string 'database' is required.");
            options.UseNpgsql(connectionString);
        });
        services.AddScoped<IMonitoringConfigLog, MonitoringConfigLog>();
        services.AddScoped<IMonitoredDeviceService, MonitoredDeviceService>();
        services.AddScoped<IMaintenanceWindowService, MaintenanceWindowService>();
        services.AddScoped<IPollerService, PollerService>();
        services.AddScoped<IPollerHeartbeatService, PollerHeartbeatService>();

        services.AddOptions<PollerHeartbeatOptions>()
            .Bind(configuration.GetSection(PollerHeartbeatOptions.SectionName))
            .Validate(options => options.MissedThreshold >= 1,
                $"{PollerHeartbeatOptions.SectionName}:MissedThreshold must be at least 1.")
            .Validate(options => options.DefaultIntervalSeconds >= 1,
                $"{PollerHeartbeatOptions.SectionName}:DefaultIntervalSeconds must be at least 1.")
            .Validate(options => options.EvaluationIntervalSeconds >= 1,
                $"{PollerHeartbeatOptions.SectionName}:EvaluationIntervalSeconds must be at least 1.")
            .ValidateOnStart();

        var heartbeat = configuration.GetSection(PollerHeartbeatOptions.SectionName)
            .Get<PollerHeartbeatOptions>() ?? new PollerHeartbeatOptions();
        services.AddQuartz(quartz =>
        {
            // Frequent and cheap: one indexed query over a handful of rows. The interval is the
            // detection granularity, not the threshold — a poller is reported between two of its own
            // cycles and two cycles plus this.
            var jobKey = new JobKey("poller-heartbeat");
            quartz.AddJob<PollerHeartbeatJob>(builder => builder.WithIdentity(jobKey));
            quartz.AddTrigger(builder => builder.ForJob(jobKey).WithIdentity("poller-heartbeat-evaluation")
                .StartNow().WithSimpleSchedule(schedule => schedule
                    .WithIntervalInSeconds(heartbeat.EvaluationIntervalSeconds).RepeatForever()));
        });

        return services;
    }

    /// <summary>
    /// The consumers this module owns. It is handed to Platform's bus registration rather than
    /// registered here, because MassTransit is configured once for the whole host — and a consumer
    /// still belongs in the module that reacts, not in the one that owns the transport.
    /// </summary>
    public static void AddMonitoringConsumers(IBusRegistrationConfigurator bus)
    {
        ArgumentNullException.ThrowIfNull(bus);
        bus.AddConsumer<PollerHeartbeatConsumer>();
    }
}
