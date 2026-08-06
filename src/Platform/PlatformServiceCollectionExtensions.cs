using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Platform.Auditing;
using Platform.Data;
using Platform.Notifications;
using Platform.Scheduling;

using Quartz;

namespace Platform;

public static class PlatformServiceCollectionExtensions
{
    public static IServiceCollection AddPlatformServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddHttpContextAccessor();
        services.AddDbContext<PlatformDbContext>((provider, options) =>
        {
            var connectionString = provider.GetRequiredService<IConfiguration>().GetConnectionString("database")
                ?? throw new InvalidOperationException("Connection string 'database' is required.");
            options.UseNpgsql(connectionString);
        });
        services.AddScoped<IAuditService, AuditService>();
        services.AddSingleton<INotificationService, LoggingNotificationService>();
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