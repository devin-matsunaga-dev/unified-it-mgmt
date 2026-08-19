using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using Modules.Monitoring.Data;
using Modules.Monitoring.Features.Devices;

namespace Modules.Monitoring.Features.Discovery;

public interface IScanDispatchService
{
    /// <summary>
    /// Hands a scanner the runs waiting for its group, and marks them as its own. The only place
    /// anything is ever told to sweep a range out of schedule, and it answers a <em>fetch</em> — the
    /// scanner asks, the platform never pushes.
    /// </summary>
    Task<ScanDispatchResult> ClaimAsync(
        string discoveryGroup,
        string discoveryName,
        CancellationToken cancellationToken);

    Task<ScanReportResult> ReportAsync(
        string discoveryGroup,
        Guid scanRunId,
        ReportScanRunRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Records how far a sweep has got. Accepted only while the run is <c>Running</c>, and it moves
    /// nothing but the progress columns — a scanner cannot finish a run by reporting progress on it.
    /// </summary>
    Task<ScanReportResult> ReportProgressAsync(
        string discoveryGroup,
        Guid scanRunId,
        ReportScanProgressRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// The agent side of on-demand scanning, and the reason there is no queue.
/// <para>
/// ARCHITECTURE §4 gives the discovery service publish-only bus credentials and one read-only config
/// fetch, and says agents never consume commands. A requested scan therefore travels the way its
/// configuration already does: the scanner asks over HTTP under its own <c>CanDiscover</c> identity,
/// gets what is waiting for its group, and posts the result back. Nothing new is granted to it — it
/// cannot request a scan, only collect one a person asked for and report what happened.
/// </para>
/// <para>
/// Two scanners may share a group, so claiming is a conditional update rather than a read followed by
/// a write: the row's status and scanner name move in one statement, and a scanner that lost the race
/// sees a row carrying somebody else's name and leaves it alone. WP-5.6's dispatch does the same and
/// for the same reason.
/// </para>
/// </summary>
public sealed class ScanDispatchService(
    MonitoringDbContext dbContext,
    IOptions<DiscoveryOptions> options) : IScanDispatchService
{
    private readonly DiscoveryOptions _options = options.Value;

    public async Task<ScanDispatchResult> ClaimAsync(
        string discoveryGroup,
        string discoveryName,
        CancellationToken cancellationToken)
    {
        var group = Normalise(discoveryGroup);
        var now = DateTimeOffset.UtcNow;
        var deadline = now.AddMinutes(_options.RunTimeoutMinutes);

        var candidates = await dbContext.ScanRuns.AsNoTracking()
            .Where(run => run.DiscoveryGroup == group && run.Status == ScanRunStatus.Queued)
            .OrderBy(run => run.RequestedAt).ThenBy(run => run.Id)
            .Take(_options.DispatchBatchSize)
            .Select(run => run.Id)
            .ToListAsync(cancellationToken);

        var claimed = new List<ScanDispatchItem>();
        foreach (var id in candidates)
        {
            // `Status == Queued` inside the update is the whole of the concurrency control: whichever
            // scanner's statement lands first moves the row, and the other updates nothing.
            var moved = await dbContext.ScanRuns
                .Where(run => run.Id == id && run.Status == ScanRunStatus.Queued)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(run => run.Status, ScanRunStatus.Running)
                    .SetProperty(run => run.DiscoveryName, discoveryName)
                    .SetProperty(run => run.DispatchedAt, now)
                    .SetProperty(run => run.DeadlineAt, deadline),
                    cancellationToken);
            if (moved == 0)
            {
                continue;
            }

            // Read the profile after the claim rather than before it, so a scanner never carries away a
            // profile it did not win. A profile deleted between the two cascades the run away with it,
            // which is why this can legitimately find nothing.
            var profile = await dbContext.ScanRuns.AsNoTracking()
                .Where(run => run.Id == id)
                .Select(run => run.ScanProfile)
                .SingleOrDefaultAsync(cancellationToken);
            if (profile is null)
            {
                continue;
            }

            claimed.Add(new(id, deadline, ScanProfileService.ToConfig(profile)));
        }

        return new(MonitoringOutcome.Success, new ScanDispatchResponse(group, claimed, now));
    }

    public async Task<ScanReportResult> ReportAsync(
        string discoveryGroup,
        Guid scanRunId,
        ReportScanRunRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Succeeded or Failed only. Queued and Running are not outcomes, and TimedOut is the platform's
        // own verdict about this scanner — letting an agent report it would let a scanner that gave up
        // describe itself as having been abandoned.
        if (!Enum.TryParse<ScanRunStatus>(request.Outcome, ignoreCase: true, out var status)
            || status is not (ScanRunStatus.Succeeded or ScanRunStatus.Failed))
        {
            return new(
                MonitoringOutcome.Invalid,
                Errors: new Dictionary<string, string[]>
                {
                    ["outcome"] = ["Outcome must be 'Succeeded' or 'Failed'."],
                });
        }

        var group = Normalise(discoveryGroup);
        var run = await dbContext.ScanRuns.SingleOrDefaultAsync(
            item => item.Id == scanRunId && item.DiscoveryGroup == group, cancellationToken);
        if (run is null)
        {
            return new(MonitoringOutcome.NotFound);
        }

        if (run.Status is not ScanRunStatus.Running)
        {
            // The sweeper already timed it out, or another scanner reported first. The first terminal
            // state is the true one — this does not argue with it.
            return new(
                MonitoringOutcome.Duplicate,
                Error: $"Scan run {scanRunId} was already recorded as {run.Status}.");
        }

        run.Status = status;
        run.CompletedAt = DateTimeOffset.UtcNow;
        run.AddressesProbed = request.AddressesProbed;
        run.DevicesFound = request.DevicesFound;
        run.Error = Truncated(request.Error);
        await dbContext.SaveChangesAsync(cancellationToken);

        return new(MonitoringOutcome.Success, ScanRunService.Map(run));
    }

    public async Task<ScanReportResult> ReportProgressAsync(
        string discoveryGroup,
        Guid scanRunId,
        ReportScanProgressRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.AddressesProbed < 0
            || request.AddressesTotal < 0
            || request.DevicesFound < 0)
        {
            return new(
                MonitoringOutcome.Invalid,
                Errors: new Dictionary<string, string[]>
                {
                    ["addressesProbed"] = ["Counts cannot be negative."],
                });
        }

        var group = Normalise(discoveryGroup);
        var run = await dbContext.ScanRuns.SingleOrDefaultAsync(
            item => item.Id == scanRunId && item.DiscoveryGroup == group, cancellationToken);
        if (run is null)
        {
            return new(MonitoringOutcome.NotFound);
        }

        // A terminal run does not un-finish because a slow progress post arrived after the result. The
        // scanner is told so and stops posting rather than dragging a finished row backwards.
        if (run.Status is not ScanRunStatus.Running)
        {
            return new(
                MonitoringOutcome.Duplicate,
                Error: $"Scan run {scanRunId} is {run.Status} and is no longer collecting progress.");
        }

        run.AddressesProbed = request.AddressesProbed;
        run.AddressesTotal = request.AddressesTotal ?? run.AddressesTotal;
        run.DevicesFound = request.DevicesFound ?? run.DevicesFound;
        run.LastRespondingAddress = Address(request.LastRespondingAddress) ?? run.LastRespondingAddress;
        run.ProgressAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);

        return new(MonitoringOutcome.Success, ScanRunService.Map(run));
    }

    /// <summary>Bounded like every other string a scanner sends. An address is short or it is not one.</summary>
    private static string? Address(string? address) =>
        string.IsNullOrWhiteSpace(address) ? null
            : address.Trim() is { Length: <= 100 } trimmed ? trimmed : null;

    /// <summary>Bounded on the way in, because the column is bounded and a scanner is not trusted to be brief.</summary>
    private static string? Truncated(string? error) =>
        string.IsNullOrWhiteSpace(error) ? null
            : error.Length <= 4_000 ? error.Trim() : error[..4_000];

    private static string Normalise(string? discoveryGroup) =>
        string.IsNullOrWhiteSpace(discoveryGroup)
            ? ScanProfileService.DefaultDiscoveryGroup
            : discoveryGroup.Trim();
}
