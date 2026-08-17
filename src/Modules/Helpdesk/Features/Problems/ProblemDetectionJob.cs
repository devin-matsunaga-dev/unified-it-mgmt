using System.Security.Claims;

using Microsoft.Extensions.Options;

using Quartz;

namespace Modules.Helpdesk.Features.Problems;

/// <summary>
/// The nightly pass the WP asks for.
/// <para>
/// A Quartz job beside the other five rather than anything new: WP-3.2's missed heartbeats, WP-5.6's
/// runbook timeouts and WP-2.9's contract expiries are all the same shape — something nobody is going to
/// notice unless the platform goes and looks. Noticing that incidents cluster is exactly that.
/// </para>
/// <para>
/// <see cref="DisallowConcurrentExecution"/> because two passes counting the same window would race for
/// the same subject; the filtered unique index behind them means the loser writes nothing rather than a
/// duplicate, but there is no reason to make it fight.
/// </para>
/// </summary>
[DisallowConcurrentExecution]
public sealed class ProblemDetectionJob(
    IProblemSuggestionService suggestions,
    IOptions<ProblemDetectionOptions> options) : IJob
{
    /// <summary>
    /// Who the pass writes as. The same arrangement WP-3.6's automation uses for a ticket nobody asked
    /// for: a suggestion has an author, and saying it was the platform is the honest answer.
    /// </summary>
    internal static readonly ClaimsPrincipal SystemActor = new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, "system:problem-detection"),
            new Claim(ClaimTypes.Name, "Problem detection"),
        ],
        "ProblemDetection"));

    public async Task Execute(IJobExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!options.Value.Enabled)
        {
            return;
        }

        await suggestions.DetectAsync(SystemActor, context.CancellationToken);
    }
}
