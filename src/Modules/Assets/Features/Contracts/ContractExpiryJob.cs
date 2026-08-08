using Quartz;

namespace Modules.Assets.Features.Contracts;

/// <summary>
/// Runs the renewal/expiry pass once a day. The pass is idempotent, so the trigger starting it at
/// host start-up — which is what makes a notice visible without waiting until tomorrow — cannot
/// raise the same notice twice.
/// </summary>
[DisallowConcurrentExecution]
public sealed class ContractExpiryJob(IContractExpiryService service) : IJob
{
    public Task Execute(IJobExecutionContext context) => service.RunAsync(context.CancellationToken);
}
