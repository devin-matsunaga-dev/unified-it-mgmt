using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

using Modules.Assets.Data;
using Modules.Assets.Features.Topology;

namespace Modules.Assets.Features.Drift;

/// <summary>
/// Assembles the drift report from the two halves the platform already keeps apart: what an operator
/// typed into a CI, and what the last scan observed about it.
/// <para>
/// It writes nothing, and it must not. Every "fix" this report suggests is a decision — moving a CI to
/// the site its device claims to be in, or accepting that the device is misconfigured — and a report
/// that silently applied either would be back to a CMDB nobody can audit.
/// </para>
/// </summary>
public sealed class DriftService(
    AssetsDbContext dbContext,
    ITopologyService topologyService,
    IConfiguration configuration) : IDriftService
{
    /// <summary>
    /// How long a CI may go unreported before the report calls it missing, unless configuration or the
    /// request says otherwise.
    /// </summary>
    public const string StaleAfterDaysKey = "Assets:Drift:StaleAfterDays";

    internal const int MaximumPageSize = 200;

    /// <summary>
    /// The most unrecorded links the report carries. A cable nobody wrote down is a finding; four
    /// hundred of them is an estate whose relationships were never recorded at all, and the answer to
    /// that is the topology map rather than a longer list.
    /// </summary>
    internal const int MaximumUnrecordedLinks = 200;

    public async Task<DriftReportResponse> GetAsync(DriftReportRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var now = DateTimeOffset.UtcNow;
        var staleAfterDays = request.StaleAfterDays
            ?? configuration.GetValue<int?>(StaleAfterDaysKey)
            ?? DriftAnalyzer.DefaultStaleAfterDays;

        var subjects = await LoadSubjectsAsync(request.SiteId, cancellationToken);
        var items = new List<CiDriftResponse>();
        var counts = new Dictionary<DriftFindingKind, int>();

        foreach (var subject in subjects)
        {
            var findings = DriftAnalyzer.Analyse(subject, now, staleAfterDays);
            foreach (var finding in findings)
            {
                counts[finding.Kind] = counts.GetValueOrDefault(finding.Kind) + 1;
            }

            // The filters narrow a CI's findings rather than choosing between CIs, so a report filtered
            // to `Changed` shows the changed fields of every CI that has one and drops the rest of that
            // CI's row — never a row whose findings are all of another kind.
            var kept = findings
                .Where(finding => request.Kind is null || finding.Kind == request.Kind)
                .Where(finding => request.Field is null
                    || string.Equals(finding.Field, request.Field, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (kept.Count == 0)
            {
                continue;
            }

            items.Add(new CiDriftResponse(
                subject.CiId,
                subject.Name,
                subject.Type,
                subject.SiteName,
                subject.Observation.Address,
                subject.Observation.LastSeenAt,
                [.. kept.Select(finding => new DriftFindingResponse(
                    finding.Field,
                    DriftFields.LabelOf(finding.Field),
                    finding.Kind,
                    finding.RecordedValue,
                    finding.ObservedValue))]));
        }

        // Worst first: a CI with four disagreements is the one to open, and within that by name so the
        // order does not depend on how the database felt about returning rows.
        items = [.. items
            .OrderByDescending(item => item.Findings.Count)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.CiId)];

        var page = Math.Max(request.Page, 1);
        var pageSize = Math.Clamp(request.PageSize, 1, MaximumPageSize);
        var (unrecordedLinks, unrecordedLinkCount) = await LoadUnrecordedLinksAsync(cancellationToken);

        // The review queue's own count, not a second copy of its rules: a discovery nothing in the CMDB
        // answers to is "new" in the widest sense the WP text asks for, and the place it becomes a CI
        // is that queue.
        var unmatched = await dbContext.DiscoveredDevices
            .CountAsync(device => device.Status == DiscoveredDeviceStatus.Pending, cancellationToken);

        return new DriftReportResponse(
            new DriftSummaryResponse(
                subjects.Count,
                items.Count,
                counts.GetValueOrDefault(DriftFindingKind.Changed),
                counts.GetValueOrDefault(DriftFindingKind.New),
                counts.GetValueOrDefault(DriftFindingKind.Missing),
                unrecordedLinkCount,
                unmatched,
                staleAfterDays,
                now),
            [.. items.Skip((page - 1) * pageSize).Take(pageSize)],
            unrecordedLinks,
            items.Count,
            page,
            pageSize);
    }

    /// <summary>
    /// Every CI a scan has reported, with both halves loaded side by side. The join is driven from the
    /// facts rather than from the CIs on purpose: a CMDB of ten thousand laptops has a few hundred
    /// discovery rows, and a CI no scan has ever seen has nothing to compare against.
    /// </summary>
    private async Task<IReadOnlyList<DriftSubject>> LoadSubjectsAsync(
        Guid? siteId,
        CancellationToken cancellationToken)
    {
        // Scoped before the projection rather than after it: a predicate over the projected shape is
        // one EF cannot translate, and it fails as a 500 on exactly the request that carries a filter.
        var cis = dbContext.Cis.AsNoTracking();
        if (siteId is { } scope)
        {
            cis = cis.Where(ci => ci.SiteId == scope);
        }

        var query =
            from facts in dbContext.CiDiscoveryFacts.AsNoTracking()
            join ci in cis on facts.CiId equals ci.Id
            select new SubjectRow(
                ci.Id,
                ci.Name,
                EF.Property<CiType>(ci, "CiType"),
                ci.SiteId,
                ci.SiteName,
                ci is ServerCi ? ((ServerCi)ci).Hostname : ci is VirtualCi ? ((VirtualCi)ci).Hostname : null,
                ci is NetworkDeviceCi ? ((NetworkDeviceCi)ci).ManagementIp : null,
                facts.Address,
                facts.Hostname,
                facts.SysName,
                facts.SysLocation,
                facts.SysDescription,
                facts.LastSeenAt);

        var rows = await query.ToListAsync(cancellationToken);
        return
        [
            .. rows.Select(row => new DriftSubject(
                row.CiId,
                row.Name,
                row.Type,
                row.SiteId,
                row.SiteName,
                // A type that records no hostname passes null, which is what tells the comparator to
                // skip the field rather than report it missing on every switch in the estate.
                DriftAnalyzer.RecordsHostname(row.Type) ? row.RecordedHostname ?? string.Empty : null,
                DriftAnalyzer.RecordsManagementIp(row.Type) ? row.RecordedManagementIp ?? string.Empty : null,
                new DriftObservation(
                    row.Address,
                    row.FactsHostname,
                    row.SysName,
                    row.SysLocation,
                    row.SysDescription,
                    // The system group answered if any of it came back. sysName is the field every
                    // agent sets; sysDescription is the one that survives a device with no name.
                    AnsweredSnmp: row.SysName is not null || row.SysDescription is not null,
                    row.LastSeenAt))),
        ];
    }

    /// <summary>
    /// The cables WP-4.3 draws as dashed lines: observed by a scan, recorded by nobody. Reconciled by
    /// that package's own resolver rather than a second copy of it.
    /// </summary>
    /// <returns>
    /// The links to render and how many there are in total. The two differ once the estate has more
    /// unrecorded cabling than the report will list, and the summary carries the honest number —
    /// WP-2.4's rule that a truncated answer must never look like a complete one.
    /// </returns>
    private async Task<(IReadOnlyList<UnrecordedLinkResponse> Links, int Total)> LoadUnrecordedLinksAsync(
        CancellationToken cancellationToken)
    {
        var reconciliation = await topologyService.ReconcileObservedLinksAsync(cancellationToken);
        var unrecorded = reconciliation.Links.Where(link => !link.MatchesAssertedEdge).ToList();
        if (unrecorded.Count == 0)
        {
            return ([], 0);
        }

        var ends = unrecorded
            .SelectMany(link => new[] { link.SourceCiId, link.TargetCiId })
            .Distinct()
            .ToList();
        var names = await dbContext.Cis.AsNoTracking()
            .Where(ci => ends.Contains(ci.Id))
            .Select(ci => new { ci.Id, ci.Name })
            .ToDictionaryAsync(ci => ci.Id, ci => ci.Name, cancellationToken);

        return (
        [
            .. unrecorded
                .Select(link => new UnrecordedLinkResponse(
                    link.SourceCiId,
                    names.GetValueOrDefault(link.SourceCiId, "Unknown CI"),
                    link.SourcePort,
                    link.TargetCiId,
                    names.GetValueOrDefault(link.TargetCiId, "Unknown CI"),
                    link.TargetPort,
                    link.Protocols,
                    link.ConfirmedByBothEnds))
                .OrderBy(link => link.SourceCiName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(link => link.TargetCiName, StringComparer.OrdinalIgnoreCase)
                .Take(MaximumUnrecordedLinks),
        ], unrecorded.Count);
    }

    private sealed record SubjectRow(
        Guid CiId,
        string Name,
        CiType Type,
        Guid? SiteId,
        string? SiteName,
        string? RecordedHostname,
        string? RecordedManagementIp,
        string Address,
        string? FactsHostname,
        string? SysName,
        string? SysLocation,
        string? SysDescription,
        DateTimeOffset LastSeenAt);
}
