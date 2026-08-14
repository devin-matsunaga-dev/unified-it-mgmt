using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Platform.Auditing;
using Platform.Data;
using Platform.Directory;
using Platform.Integration;
using Platform.Notifications;
using Platform.Scheduling;
using Platform.Messaging;
using Platform.Vault;

using MassTransit;
using Quartz;

namespace Platform;

public static class PlatformServiceCollectionExtensions
{
    /// <param name="configureBus">
    /// Consumers owned by the modules. MassTransit is configured exactly once for the host, but a
    /// consumer belongs to the module that reacts — so each module hands its own registrations in
    /// here rather than Platform naming them, which it could not do without referencing them.
    /// </param>
    public static IServiceCollection AddPlatformServices(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<IBusRegistrationConfigurator>? configureBus = null)
    {
        services.AddHttpContextAccessor();
        services.AddDbContext<PlatformDbContext>((provider, options) =>
        {
            var connectionString = provider.GetRequiredService<IConfiguration>().GetConnectionString("database")
                ?? throw new InvalidOperationException("Connection string 'database' is required.");
            options.UseNpgsql(connectionString);
        });
        services.AddScoped<IAuditService, AuditService>();
        services.AddScoped<IDirectoryService, DirectoryService>();
        AddCredentialVault(services);
        services.AddScoped<IConsumerIdempotencyService, ConsumerIdempotencyService>();
        services.AddScoped<ISystemPingPublisher, SystemPingPublisher>();
        services.AddMassTransit(bus =>
        {
            bus.AddConsumer<SystemPingConsumer>();
            configureBus?.Invoke(bus);
            bus.AddConfigureEndpointsCallback((context, _, endpoint) =>
                endpoint.UseEntityFrameworkOutbox<PlatformDbContext>(context));
            bus.AddEntityFrameworkOutbox<PlatformDbContext>(outbox =>
            {
                outbox.UsePostgres();
                outbox.UseBusOutbox();
            });
            if (!configuration.GetValue("Platform:EnableMessageBus", true))
            {
                bus.UsingInMemory((context, inMemory) => inMemory.ConfigureEndpoints(context));
            }
            else
            {
                bus.UsingRabbitMq((context, rabbit) =>
                {
                    var connectionString = context.GetRequiredService<IConfiguration>()
                        .GetConnectionString("rabbitmq")
                        ?? throw new InvalidOperationException("Connection string 'rabbitmq' is required.");
                    rabbit.Host(new Uri(connectionString));
                    rabbit.ConfigureEndpoints(context);
                });
            }
        });
        services.AddScoped<INotificationService, SmtpNotificationService>();

        // WP-3.10. The channels are registered as one enumerable and picked by Kind: the router asks
        // for "the Teams channel", not for a specific implementation, so adding a fourth kind is a
        // registration rather than an edit to the router.
        services.AddHttpClient();
        services.AddScoped<INotificationChannel, EmailNotificationChannel>();
        services.AddScoped<INotificationChannel>(provider => new WebhookNotificationChannel(
            NotificationChannelKind.Teams,
            provider.GetRequiredService<IHttpClientFactory>(),
            provider.GetRequiredService<ILogger<WebhookNotificationChannel>>()));
        services.AddScoped<INotificationChannel>(provider => new WebhookNotificationChannel(
            NotificationChannelKind.Slack,
            provider.GetRequiredService<IHttpClientFactory>(),
            provider.GetRequiredService<ILogger<WebhookNotificationChannel>>()));
        services.AddScoped<INotificationRouter, NotificationRouter>();
        services.AddScoped<INotificationRoutingService, NotificationRoutingService>();
        services.AddScoped<INotificationDigestService, NotificationDigestService>();
        services.AddOptions<VaultOptions>()
            .Bind(configuration.GetSection(VaultOptions.SectionName))
            .Validate(options => options.GrantLifetimeSeconds >= 1,
                $"{VaultOptions.SectionName}:GrantLifetimeSeconds must be at least 1.")
            .ValidateOnStart();

        services.AddOptions<NotificationOptions>()
            .Bind(configuration.GetSection(NotificationOptions.SectionName))
            .Validate(options => options.DigestIntervalSeconds >= 1,
                $"{NotificationOptions.SectionName}:DigestIntervalSeconds must be at least 1.")
            .ValidateOnStart();

        var notifications = configuration.GetSection(NotificationOptions.SectionName).Get<NotificationOptions>()
            ?? new NotificationOptions();
        services.AddQuartz(quartz =>
        {
            var jobKey = new JobKey("platform-heartbeat");
            quartz.AddJob<PlatformHeartbeatJob>(options => options.WithIdentity(jobKey));
            quartz.AddTrigger(options => options
                .ForJob(jobKey)
                .WithIdentity("platform-heartbeat-every-minute")
                .StartNow()
                .WithSimpleSchedule(schedule => schedule.WithIntervalInMinutes(1).RepeatForever()));

            // Cheap: one indexed query that usually finds nothing. The interval is the granularity of
            // "when quiet hours end", so a digest lands within one of these of the window closing.
            var digestKey = new JobKey("notification-digest");
            quartz.AddJob<NotificationDigestJob>(options => options.WithIdentity(digestKey));
            quartz.AddTrigger(options => options
                .ForJob(digestKey)
                .WithIdentity("notification-digest-release")
                .StartNow()
                .WithSimpleSchedule(schedule => schedule
                    .WithIntervalInSeconds(notifications.DigestIntervalSeconds).RepeatForever()));
        });
        services.AddSingleton<IHostedService, PlatformSchedulerHostedService>();

        return services;
    }

    /// <summary>
    /// WP-3.11. Registered from here rather than from a module, because a credential is a platform
    /// fact — the poller authenticates with it, Monitoring only names it — and ARCHITECTURE §3 puts
    /// the vault in Platform's ownership map.
    /// </summary>
    private static void AddCredentialVault(IServiceCollection services)
    {
        // The key ring lives in Postgres beside the ciphertext it protects. The default is the host's
        // own filesystem, which in a container means a fresh key on every restart and every stored
        // credential becoming undecryptable — silently, because nothing fails until something tries
        // to poll. `SetApplicationName` pins the other half: the ring is namespaced by application
        // name, so two hosts sharing this database have to agree on it to read each other's keys.
        services.AddSingleton<DataProtectionKeyRepository>();
        services.AddDataProtection()
            .SetApplicationName("it-platform")
            .Services
            .AddSingleton<IConfigureOptions<KeyManagementOptions>>(provider =>
                new ConfigureNamedOptions<KeyManagementOptions>(Options.DefaultName, keyManagement =>
                    keyManagement.XmlRepository = provider.GetRequiredService<DataProtectionKeyRepository>()));

        services.AddScoped<ICredentialProtector, CredentialProtector>();
        services.AddScoped<ICredentialVault, CredentialVault>();

        // Monitoring replaces this wherever it is registered. TryAdd so that whichever registration
        // runs second does not win by accident — the same shape as `IMonitoringBroadcaster`.
        services.TryAddScoped<ICredentialUsageDirectory, NoCredentialUsageDirectory>();

        // Same arrangement for the discovery match ladder's top rung (WP-4.2): Assets asks which CI is
        // already monitored at an address, and a host without Monitoring gets an honest "none" instead
        // of a start-up DI failure.
        services.TryAddScoped<IMonitoredAddressDirectory, NoMonitoredAddressDirectory>();

        // WP-5.1's two, on the same terms. Both fall back toward saying more rather than less: without
        // Assets the correlator finds no dependencies and therefore suppresses nothing, and without
        // Monitoring a root-cause ticket lists no impacted CIs and is an ordinary alert ticket.
        services.TryAddScoped<ICiDependencyDirectory, NoCiDependencyDirectory>();
        services.TryAddScoped<IAlertCorrelationDirectory, NoAlertCorrelationDirectory>();
    }
}
