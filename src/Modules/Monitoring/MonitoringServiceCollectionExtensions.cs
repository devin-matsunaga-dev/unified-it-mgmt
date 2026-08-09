using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Modules.Monitoring.Data;
using Modules.Monitoring.Features.Devices;
using Modules.Monitoring.Features.MaintenanceWindows;
using Modules.Monitoring.Features.PollerConfig;

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

        return services;
    }
}
