using MassTransit;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modules.Monitoring.Data;
using Modules.Monitoring.Features.Alerting;
using Modules.Monitoring.Features.Dashboards;
using Modules.Monitoring.Features.Devices;
using Modules.Monitoring.Features.Discovery;
using Modules.Monitoring.Features.Heartbeats;
using Modules.Monitoring.Features.Interfaces;
using Modules.Monitoring.Features.MaintenanceWindows;
using Modules.Monitoring.Features.Metrics;
using Modules.Monitoring.Features.PollerConfig;
using Modules.Monitoring.Features.Runbooks;
using Modules.Monitoring.Features.Search;
using Platform.Dashboards;
using Platform.Integration;
using Platform.Search;
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
        services.AddScoped<IMaintenanceSyncService, MaintenanceSyncService>();
        services.AddScoped<IPollerService, PollerService>();
        services.AddScoped<IScanProfileService, ScanProfileService>();
        services.AddScoped<IPollerCredentialService, PollerCredentialService>();
        // The vault's delete guard, replacing Platform's "nothing uses any credential" default. Not
        // TryAdd: this module is the authority on the question, and a host that registers Monitoring
        // must get the real answer.
        services.AddScoped<ICredentialUsageDirectory, CredentialUsageDirectory>();
        // The top rung of WP-4.2's match ladder, on the same terms: this module knows what is monitored
        // where, so a host that registers it must get the real answer rather than Platform's "none".
        services.AddScoped<IMonitoredAddressDirectory, MonitoredAddressDirectory>();
        services.AddScoped<IDiscoveryEnrollmentService, DiscoveryEnrollmentService>();
        services.AddScoped<IPollerHeartbeatService, PollerHeartbeatService>();
        services.AddScoped<IMetricIngestionService, MetricIngestionService>();
        services.AddScoped<IMetricQueryService, MetricQueryService>();
        services.AddScoped<IInterfaceService, InterfaceService>();
        services.AddScoped<IAlertStateStore, RedisAlertStateStore>();
        services.AddScoped<IAlertEnrichmentService, AlertEnrichmentService>();
        services.AddScoped<IAlertEngine, AlertEngine>();
        services.AddScoped<IAlertCorrelationDirectory, AlertCorrelationDirectory>();
        services.AddScoped<ICiAlertHistoryDirectory, CiAlertHistoryDirectory>();
        services.AddScoped<IAlertService, AlertService>();
        // WP-5.4. Two sources from one module: a device and what is wrong with it are different
        // questions and land in different groups.
        services.AddScoped<ISearchSource, DeviceSearchSource>();
        services.AddScoped<ISearchSource, AlertSearchSource>();
        services.AddScoped<IStatusBoardService, StatusBoardService>();
        // WP-5.5. Two widgets from one module, registered here for the same reason the search sources
        // are: the queries are over Monitoring's own schema, and the dashboard service that composes
        // them holds no reference to this module.
        services.AddScoped<IDashboardWidget, NetworkStatusWidget>();
        services.AddScoped<IDashboardWidget, RecentRootCausesWidget>();
        // WP-5.6. Four services rather than one, following the audiences: the registry is administered,
        // executions are requested and read, the channel is an agent's, and completion is shared by the
        // channel and the sweeper so a timed-out run escalates exactly like a failed one.
        services.AddScoped<IRunbookRegistryService, RunbookRegistryService>();
        services.AddScoped<IRunbookExecutionService, RunbookExecutionService>();
        services.AddScoped<IRunbookCompletionService, RunbookCompletionService>();
        services.AddScoped<IRunbookDispatchService, RunbookDispatchService>();
        services.AddScoped<IRunbookTimeoutSweeper, RunbookTimeoutSweeper>();
        services.AddScoped<IMonitoringLiveUpdateService, MonitoringLiveUpdateService>();
        services.AddScoped<IAlertNotificationService, AlertNotificationService>();

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

        services.AddOptions<RunbookOptions>()
            .Bind(configuration.GetSection(RunbookOptions.SectionName))
            .Validate(options => options.DispatchBatchSize >= 1,
                $"{RunbookOptions.SectionName}:DispatchBatchSize must be at least 1.")
            .Validate(options => options.DefaultTimeoutSeconds >= 1,
                $"{RunbookOptions.SectionName}:DefaultTimeoutSeconds must be at least 1.")
            .Validate(options => options.MaximumTimeoutSeconds >= options.DefaultTimeoutSeconds,
                $"{RunbookOptions.SectionName}:MaximumTimeoutSeconds must be at least DefaultTimeoutSeconds.")
            .Validate(options => options.DefaultMaxExecutionsPerWindow >= 1,
                $"{RunbookOptions.SectionName}:DefaultMaxExecutionsPerWindow must be at least 1 — disable a runbook to stop it running.")
            .Validate(options => options.DefaultRateLimitWindowMinutes >= 1,
                $"{RunbookOptions.SectionName}:DefaultRateLimitWindowMinutes must be at least 1.")
            .Validate(options => options.MaximumOutputCharacters >= 1,
                $"{RunbookOptions.SectionName}:MaximumOutputCharacters must be at least 1.")
            .Validate(options => options.SweepIntervalSeconds >= 1,
                $"{RunbookOptions.SectionName}:SweepIntervalSeconds must be at least 1.")
            .ValidateOnStart();

        var heartbeat = configuration.GetSection(PollerHeartbeatOptions.SectionName)
            .Get<PollerHeartbeatOptions>() ?? new PollerHeartbeatOptions();
        var runbooks = configuration.GetSection(RunbookOptions.SectionName)
            .Get<RunbookOptions>() ?? new RunbookOptions();
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

            // WP-5.6. Frequent and usually empty: one indexed query over rows that exist only while a
            // remediation is in flight. The interval is how late a timeout can be noticed, not how long
            // a runbook may run — that is the runbook's own deadline, stamped when it was handed over.
            var runbookJobKey = new JobKey("runbook-timeouts");
            quartz.AddJob<RunbookTimeoutJob>(builder => builder.WithIdentity(runbookJobKey));
            quartz.AddTrigger(builder => builder.ForJob(runbookJobKey).WithIdentity("runbook-timeout-sweep")
                .StartNow().WithSimpleSchedule(schedule => schedule
                    .WithIntervalInSeconds(runbooks.SweepIntervalSeconds).RepeatForever()));
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
        // Their own endpoints, beside Helpdesk's ticket automation on the same two events: a Teams
        // webhook that will not answer must not be able to stop a ticket being opened, which is the
        // same separation WP-3.5 made between telemetry ingestion and alert evaluation.
        bus.AddConsumer<AlertRaisedNotificationConsumer>();
        bus.AddConsumer<AlertClearedNotificationConsumer>();
        // Assets publishes it, Monitoring acts on it: the approval half of WP-4.2's review queue.
        bus.AddConsumer<DiscoveredDeviceApprovedConsumer>();
        // WP-5.6, on its own endpoint beside the ticket and notification consumers of the same event:
        // deciding to remediate must not be able to slow down, or fail, recording the alert.
        bus.AddConsumer<AlertRunbookConsumer>();
        // WP-5.8, the same arrangement as the discovery approval above and pointing the same
        // way: Assets agrees a change, Monitoring opens the window it implies.
        bus.AddConsumer<ChangeRequestApprovedConsumer>();
    }
}
