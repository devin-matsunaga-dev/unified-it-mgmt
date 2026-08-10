using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

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
        services.AddQuartz(quartz =>
        {
            var jobKey = new JobKey("platform-heartbeat");
            quartz.AddJob<PlatformHeartbeatJob>(options => options.WithIdentity(jobKey));
            quartz.AddTrigger(options => options
                .ForJob(jobKey)
                .WithIdentity("platform-heartbeat-every-minute")
                .StartNow()
                .WithSimpleSchedule(schedule => schedule.WithIntervalInMinutes(1).RepeatForever()));
        });
        services.AddSingleton<IHostedService, PlatformSchedulerHostedService>();

        return services;
    }
}
