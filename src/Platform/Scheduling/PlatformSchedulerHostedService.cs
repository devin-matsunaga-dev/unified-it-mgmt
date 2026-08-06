using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Quartz;

namespace Platform.Scheduling;

public sealed class PlatformSchedulerHostedService(
    IConfiguration configuration,
    IServiceProvider serviceProvider) : IHostedService
{
    private IScheduler? _scheduler;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!configuration.GetValue("Platform:EnableScheduler", true))
        {
            return;
        }

        var schedulerFactory = serviceProvider.GetRequiredService<ISchedulerFactory>();
        _scheduler = await schedulerFactory.GetScheduler(cancellationToken);
        await _scheduler.Start(cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_scheduler is not null)
        {
            await _scheduler.Shutdown(true, cancellationToken);
        }
    }
}