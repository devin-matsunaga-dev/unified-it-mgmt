using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Platform.Auditing;
using Platform.Data;
using Platform.Directory;
using Platform.Notifications;
using Platform.Scheduling;
using Platform.Messaging;

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
}
