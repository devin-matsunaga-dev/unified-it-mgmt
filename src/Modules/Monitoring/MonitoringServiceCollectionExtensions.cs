using MassTransit;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modules.Monitoring.Data;
using Modules.Monitoring.Features.Alerting;
using Modules.Monitoring.Features.Dashboards;
using Modules.Monitoring.Features.Devices;
using Modules.Monitoring.Features.Heartbeats;
using Modules.Monitoring.Features.MaintenanceWindows;
using Modules.Monitoring.Features.Metrics;
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
        services.AddScoped<IMetricIngestionService, MetricIngestionService>();
        services.AddScoped<IMetricQueryService, MetricQueryService>();
        services.AddScoped<IAlertStateStore, RedisAlertStateStore>();
        services.AddScoped<IAlertEnrichmentService, AlertEnrichmentService>();
        services.AddScoped<IAlertEngine, AlertEngine>();
        services.AddScoped<IAlertService, AlertService>();
        services.AddScoped<IStatusBoardService, StatusBoardService>();
        services.AddScoped<IMonitoringLiveUpdateService, MonitoringLiveUpdateService>();

        // A host with no SignalR hub still has to be able to construct the alert engine — a seeder, a
        // test host, a future worker. Web.Host replaces this with the hub-backed one; TryAdd, so
        // whichever registration runs second does not win by accident.
        services.TryAddScoped<IMonitoringBroadcaster, NullMonitoringBroadcaster>();

        services.AddOptions<AlertOptions>()
            .Bind(configuration.GetSection(AlertOptions.SectionName))
            .Validate(options => options.SustainedCycles >= 1,
                $"{AlertOptions.SectionName}:SustainedCycles must be at least 1.")
            .Validate(options => options.RecoveryCycles >= 1,
                $"{AlertOptions.SectionName}:RecoveryCycles must be at least 1.")
            .Validate(options => options.HysteresisPercent is >= 0 and < 100,
                $"{AlertOptions.SectionName}:HysteresisPercent must be at least 0 and below 100.")
            .Validate(options => options.FlapThreshold >= 2,
                $"{AlertOptions.SectionName}:FlapThreshold must be at least 2 — one state change is not a flap.")
            .Validate(options => options.FlapWindowSeconds >= 1,
                $"{AlertOptions.SectionName}:FlapWindowSeconds must be at least 1.")
            .Validate(options => options.FlapCooldownSeconds >= 1,
                $"{AlertOptions.SectionName}:FlapCooldownSeconds must be at least 1.")
            .Validate(options => options.StateTtlDays >= 1,
                $"{AlertOptions.SectionName}:StateTtlDays must be at least 1.")
            .ValidateOnStart();

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
        bus.AddConsumer<DeviceTelemetryConsumer>();
        bus.AddConsumer<AlertTelemetryConsumer>();
    }
}
