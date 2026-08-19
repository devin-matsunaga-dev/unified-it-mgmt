using System.Security.Claims;

using Microsoft.EntityFrameworkCore;
using Modules.Monitoring.Data;
using Modules.Monitoring.Features.Devices;
using Platform.Auditing;

namespace Modules.Monitoring.Features.Discovery;

public interface IScanRunService
{
    /// <summary>
    /// Asks for one profile to be swept now. Records the request; the scanner collects it on its own
    /// next cycle, which is why this returns a <c>Queued</c> run rather than a result.
    /// </summary>
    Task<ScanRunResult> RequestAsync(
        Guid scanProfileId,
        RequestScanRunRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken);

    Task<ScanRunPageResponse> ListAsync(ScanRunListRequest request, CancellationToken cancellationToken);

    Task<ScanRunResponse?> GetAsync(Guid id, CancellationToken cancellationToken);
}

/// <summary>
/// The operator end of on-demand scanning: ask for a run, then watch it.
/// <para>
/// It is deliberately not able to run anything. Every path here writes a row and stops — the scanning
/// itself belongs to a process this one cannot reach, and that is the whole shape ARCHITECTURE §4
/// requires of an agent channel.
/// </para>
/// </summary>
public sealed class ScanRunService(
    MonitoringDbContext dbContext,
    IAuditService auditService) : IScanRunService
{
    private const int MaximumPageSize = 200;

    public async Task<ScanRunResult> RequestAsync(
        Guid scanProfileId,
        RequestScanRunRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var profile = await dbContext.ScanProfiles.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == scanProfileId, cancellationToken);
        if (profile is null)
        {
            return new(MonitoringOutcome.NotFound);
        }

        // A disabled profile has left every scanner's configuration, so a run of it would sit queued
        // until it timed out. Refusing here says so while somebody is still looking at the button.
        if (!profile.IsEnabled)
        {
            return new(
                MonitoringOutcome.Invalid,
                Errors: new Dictionary<string, string[]>
                {
                    ["scanProfileId"] =
                        ["This profile is disabled. Enable it before asking for a scan — a disabled profile reaches no scanner."],
                });
        }

        var now = DateTimeOffset.UtcNow;
        var run = new ScanRun
        {
            Id = Guid.CreateVersion7(),
            ScanProfileId = profile.Id,
            ScanProfileName = profile.Name,
            DiscoveryGroup = profile.DiscoveryGroup,
            Status = ScanRunStatus.Queued,
            RequestedBy = GetActorId(actor),
            RequestedAt = now,
        };

        dbContext.ScanRuns.Add(run);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // The one-queued-run-per-profile index caught a second press. Answering with the run that
            // is already waiting is more useful than an error: the scan being asked for is about to
            // happen, and the caller gets something it can watch. Anything else that broke the insert
            // is rethrown — this recovers from a known collision, not from a write going wrong.
            dbContext.Entry(run).State = EntityState.Detached;
            if (await QueuedRunAsync(profile.Id, cancellationToken) is not { } queued)
            {
                throw;
            }

            return new(MonitoringOutcome.Duplicate, Map(queued),
                Error: $"A scan of '{profile.Name}' is already queued.");
        }

        var response = Map(run);
        await auditService.WriteAsync(
            actor, "Requested", "ScanRun", run.Id.ToString(), null,
            new { response.ScanProfileId, response.ScanProfileName, request.Note }, cancellationToken);
        return new(MonitoringOutcome.Success, response);
    }

    public async Task<ScanRunPageResponse> ListAsync(
        ScanRunListRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var page = Math.Max(request.Page, 1);
        var pageSize = Math.Clamp(request.PageSize, 1, MaximumPageSize);

        var query = dbContext.ScanRuns.AsNoTracking();
        if (request.ScanProfileId is { } profileId)
        {
            query = query.Where(run => run.ScanProfileId == profileId);
        }

        if (!string.IsNullOrWhiteSpace(request.Status)
            && Enum.TryParse<ScanRunStatus>(request.Status, ignoreCase: true, out var status))
        {
            query = query.Where(run => run.Status == status);
        }

        var total = await query.CountAsync(cancellationToken);
        var runs = await query
            .OrderByDescending(run => run.RequestedAt).ThenByDescending(run => run.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new([.. runs.Select(Map)], total, page, pageSize);
    }

    public async Task<ScanRunResponse?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        await dbContext.ScanRuns.AsNoTracking()
            .SingleOrDefaultAsync(run => run.Id == id, cancellationToken) is { } run
            ? Map(run)
            : null;

    private Task<ScanRun?> QueuedRunAsync(Guid scanProfileId, CancellationToken cancellationToken) =>
        dbContext.ScanRuns.AsNoTracking().FirstOrDefaultAsync(
            run => run.ScanProfileId == scanProfileId && run.Status == ScanRunStatus.Queued,
            cancellationToken);

    internal static ScanRunResponse Map(ScanRun run) =>
        new(run.Id,
            run.ScanProfileId,
            run.ScanProfileName,
            run.DiscoveryGroup,
            run.Status,
            run.RequestedBy,
            run.RequestedAt,
            run.DiscoveryName,
            run.DispatchedAt,
            run.DeadlineAt,
            run.CompletedAt,
            run.AddressesProbed,
            run.AddressesTotal,
            run.DevicesFound,
            run.LastRespondingAddress,
            run.ProgressAt,
            run.Error);

    private static string GetActorId(ClaimsPrincipal actor) =>
        actor.FindFirstValue("sub") ?? actor.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException("An authenticated actor identifier is required.");
}
