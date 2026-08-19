using System.Security.Claims;
using System.Text.Json;

using Contracts.Events;

using MassTransit;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using Modules.Assets.Data;
using Modules.Assets.Features.Cis;

using Platform.Auditing;
using Platform.Integration;

namespace Modules.Assets.Features.Discovery;

public sealed class DiscoveryReviewService(
    AssetsDbContext dbContext,
    ICiService ciService,
    IMonitoredAddressDirectory monitoredAddresses,
    IPublishEndpoint publishEndpoint,
    IAuditService auditService,
    ILogger<DiscoveryReviewService> logger) : IDiscoveryReviewService
{
    internal const int MaximumPageSize = 200;

    /// <summary>
    /// How many contenders an ambiguous match records. A rung that found forty CIs is a naming
    /// convention this heuristic has misread, and listing all forty on a card helps nobody.
    /// </summary>
    private const int MaximumContenders = 10;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<DiscoveryIntakeResult> IngestAsync(
        DeviceDiscovered discovery,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(discovery);

        var fingerprint = DiscoveryIdentity.FingerprintOf(discovery);
        var existing = await FindLedgerRowAsync(fingerprint, cancellationToken);
        var isNew = existing is null;
        var row = existing ?? NewRow(discovery, fingerprint);

        Observe(row, discovery, fingerprint, isNew);

        // A rejected identity is the ignore list, and the whole of "reject → never reappears" is that
        // this returns before any matching runs. The sighting counters still move: somebody re-reading
        // the decision months later should be able to see that the thing is still out there.
        if (row.Status is DiscoveredDeviceStatus.Rejected)
        {
            if (isNew)
            {
                dbContext.DiscoveredDevices.Add(row);
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            return new(row.Id, row.Status, row.MatchRule, row.CiId, isNew);
        }

        // Already placed on an earlier scan, by the matcher or by a human. The decision stands and the
        // ladder is not re-walked — a CI renamed since then must not re-open a settled card.
        if (row.CiId is { } settledCiId && row.Status is DiscoveredDeviceStatus.Matched or DiscoveredDeviceStatus.Approved)
        {
            if (await CiExistsAsync(settledCiId, cancellationToken))
            {
                await RefreshFactsAsync(settledCiId, discovery, cancellationToken);
                if (isNew)
                {
                    dbContext.DiscoveredDevices.Add(row);
                }

                await dbContext.SaveChangesAsync(cancellationToken);
                return new(row.Id, row.Status, row.MatchRule, settledCiId, isNew);
            }

            // The CI was deleted out from under a settled row. Nothing prevents that — no foreign key
            // spans a decision and a CI — so the card falls back to Pending rather than pointing at a
            // row that is gone.
            logger.LogInformation(
                "Discovered device {IdentityKey} pointed at CI {CiId}, which no longer exists; returning it to review.",
                row.IdentityKey, settledCiId);
            row.CiId = null;
            row.Status = DiscoveredDeviceStatus.Pending;
            row.MatchRule = DiscoveryMatchRule.None;
        }

        var match = await MatchAsync(fingerprint, cancellationToken);
        row.MatchRule = match.Rule;
        row.ContenderCiIdsJson = Serialise(match.Contenders.Take(MaximumContenders));
        if (match.CiId is { } matchedCiId)
        {
            row.CiId = matchedCiId;
            row.Status = DiscoveredDeviceStatus.Matched;
            await RefreshFactsAsync(matchedCiId, discovery, cancellationToken);
        }
        else
        {
            row.CiId = null;
            row.Status = DiscoveredDeviceStatus.Pending;
        }

        if (isNew)
        {
            dbContext.DiscoveredDevices.Add(row);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Discovery {IdentityKey} from scan {ScanId}: {Status} by {Rule}{Ci}.",
            row.IdentityKey, discovery.ScanId, row.Status, row.MatchRule,
            row.CiId is { } ci ? $" against CI {ci}" : string.Empty);
        return new(row.Id, row.Status, row.MatchRule, row.CiId, isNew);
    }

    /// <summary>
    /// The ledger row for this identity, found by any of its fields rather than by its key alone.
    /// <para>
    /// The key is tiered — sysName, else hostname, else address — so a device that gains an SNMP agent
    /// between two scans changes tier and would otherwise become a second row: a duplicate card for one
    /// device, and an ignore-list entry that stops working the moment the device starts answering. The
    /// fields are stable even when the key is not, so the lookup uses them and the key is rewritten in
    /// place afterwards.
    /// </para>
    /// </summary>
    private async Task<DiscoveredDevice?> FindLedgerRowAsync(
        DiscoveryFingerprint fingerprint,
        CancellationToken cancellationToken)
    {
        var key = DiscoveryIdentity.KeyFor(fingerprint);
        var sysName = fingerprint.SysName;
        var hostname = fingerprint.Hostname;
        var address = fingerprint.Address;

        var candidates = await dbContext.DiscoveredDevices
            .Where(device => device.IdentityKey == key
                || (sysName != null && device.SysName != null && device.SysName.ToLower() == sysName)
                || (hostname != null && device.Hostname != null && device.Hostname.ToLower() == hostname)
                || device.Address == address)
            .ToListAsync(cancellationToken);
        if (candidates.Count == 0)
        {
            return null;
        }

        // A device that names itself is identified by that name; an address is only ever corroborating
        // evidence. So a row whose sysName contradicts this one is a *different* device that has since
        // been handed the same address — the ordinary result of a DHCP lease moving — and merging the
        // two would silently rewrite one device's history with another's.
        var usable = candidates
            .Where(device => sysName is null
                || device.SysName is null
                || string.Equals(device.SysName, sysName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Prefer the strongest evidence when several rows survive: the exact key, then a name, then the
        // bare address.
        return usable.FirstOrDefault(device => device.IdentityKey == key)
            ?? usable.FirstOrDefault(device => sysName is not null
                && string.Equals(device.SysName, sysName, StringComparison.OrdinalIgnoreCase))
            ?? usable.FirstOrDefault(device => hostname is not null
                && string.Equals(device.Hostname, hostname, StringComparison.OrdinalIgnoreCase))
            ?? usable.FirstOrDefault(device => string.Equals(device.Address, address, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Walks the ladder: the monitoring port first, then the CMDB's own recorded fields.</summary>
    private async Task<DiscoveryMatch> MatchAsync(
        DiscoveryFingerprint fingerprint,
        CancellationToken cancellationToken)
    {
        var addresses = new List<string> { fingerprint.Address };
        if (fingerprint.Hostname is { } hostname)
        {
            addresses.Add(hostname);
        }

        if (fingerprint.SysName is { } sysName && !addresses.Contains(sysName, StringComparer.OrdinalIgnoreCase))
        {
            addresses.Add(sysName);
        }

        var monitoredCiId = await monitoredAddresses.FindCiByAddressAsync(addresses, cancellationToken);
        var candidates = monitoredCiId is null
            ? await LoadCandidatesAsync(fingerprint, cancellationToken)
            : [];
        return DiscoveryMatcher.Match(fingerprint, candidates, monitoredCiId);
    }

    /// <summary>
    /// The CIs any rung could possibly match, in three narrow indexed queries rather than by loading
    /// the estate and filtering in memory. Each is bounded by an exact-equality predicate, so a 60-CI
    /// demo estate and a 60,000-CI real one cost the same.
    /// </summary>
    private async Task<IReadOnlyList<CiMatchCandidate>> LoadCandidatesAsync(
        DiscoveryFingerprint fingerprint,
        CancellationToken cancellationToken)
    {
        var names = fingerprint.Names;
        var address = fingerprint.Address;
        var candidates = new List<CiMatchCandidate>();

        candidates.AddRange(await dbContext.Cis.AsNoTracking().OfType<NetworkDeviceCi>()
            .Where(ci => ci.ManagementIp.ToLower() == address.ToLower())
            .Select(ci => new CiMatchCandidate(ci.Id, ci.Name, CiType.NetworkDevice, ci.ManagementIp, null))
            .ToListAsync(cancellationToken));

        if (names.Count > 0)
        {
            var lowered = names.ToArray();
            candidates.AddRange(await dbContext.Cis.AsNoTracking().OfType<ServerCi>()
                .Where(ci => lowered.Contains(ci.Hostname.ToLower()))
                .Select(ci => new CiMatchCandidate(ci.Id, ci.Name, CiType.Server, null, ci.Hostname))
                .ToListAsync(cancellationToken));
            candidates.AddRange(await dbContext.Cis.AsNoTracking().OfType<VirtualCi>()
                .Where(ci => lowered.Contains(ci.Hostname.ToLower()))
                .Select(ci => new CiMatchCandidate(ci.Id, ci.Name, CiType.Virtual, null, ci.Hostname))
                .ToListAsync(cancellationToken));
            candidates.AddRange(await dbContext.Cis.AsNoTracking()
                .Where(ci => lowered.Contains(ci.Name.ToLower()))
                .Select(ci => new CiMatchCandidate(ci.Id, ci.Name, ci.Type, null, null))
                .ToListAsync(cancellationToken));
        }

        return candidates;
    }

    /// <summary>
    /// Upserts what the scan saw about a CI. It writes to <c>ci_discovery_facts</c> and never to the CI
    /// itself: the CMDB keeps saying what an operator asserted, this says what the network answered,
    /// and the difference between them is WP-4.6's drift report. Overwriting the CI here would destroy
    /// the very signal that package exists to find.
    /// </summary>
    private async Task RefreshFactsAsync(Guid ciId, DeviceDiscovered discovery, CancellationToken cancellationToken)
    {
        var facts = await dbContext.CiDiscoveryFacts.SingleOrDefaultAsync(
            item => item.CiId == ciId, cancellationToken);
        if (facts is null)
        {
            facts = new CiDiscoveryFacts
            {
                CiId = ciId,
                Address = discovery.Address,
                OpenPortsJson = "[]",
                NeighboursJson = "[]",
                DiscoveryName = discovery.DiscoveryName,
                ScanProfileName = discovery.ScanProfileName,
                FirstSeenAt = discovery.OccurredAt,
                SightingCount = 0,
            };
            dbContext.CiDiscoveryFacts.Add(facts);
        }

        facts.Address = discovery.Address;
        facts.Hostname = discovery.Hostname;
        facts.RespondedToPing = discovery.RespondedToPing;
        facts.OpenPortsJson = Serialise(discovery.OpenPorts);
        facts.SysName = discovery.Snmp?.SysName;
        facts.SysDescription = discovery.Snmp?.SysDescription;
        facts.SysObjectId = discovery.Snmp?.SysObjectId;
        facts.SysLocation = discovery.Snmp?.SysLocation;
        facts.SysContact = discovery.Snmp?.SysContact;
        facts.UptimeSeconds = discovery.Snmp?.UptimeSeconds;
        facts.NeighboursJson = Serialise(discovery.Neighbours);
        facts.DiscoveryName = discovery.DiscoveryName;
        facts.ScanProfileName = discovery.ScanProfileName;
        facts.LastScanId = discovery.ScanId;
        facts.LastSeenAt = discovery.OccurredAt;
        facts.SightingCount++;
    }

    public async Task<DiscoveredDevicePageResponse> ListAsync(
        DiscoveredDeviceListRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var page = Math.Max(request.Page, 1);
        var pageSize = Math.Clamp(request.PageSize, 1, MaximumPageSize);
        var query = dbContext.DiscoveredDevices.AsNoTracking().AsQueryable();

        if (request.Status is { } status)
        {
            query = query.Where(device => device.Status == status);
        }

        if (request.ScanProfileId is { } profileId)
        {
            query = query.Where(device => device.ScanProfileId == profileId);
        }

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var term = $"%{request.Search.Trim()}%";
            query = query.Where(device =>
                EF.Functions.ILike(device.Address, term)
                || (device.Hostname != null && EF.Functions.ILike(device.Hostname, term))
                || (device.SysName != null && EF.Functions.ILike(device.SysName, term))
                || (device.SysDescription != null && EF.Functions.ILike(device.SysDescription, term)));
        }

        var total = await query.CountAsync(cancellationToken);
        var rows = await query
            // Newest sighting first: a review queue is worked from the top, and the thing that answered
            // most recently is the thing most likely to still be there.
            .OrderByDescending(device => device.LastSeenAt).ThenBy(device => device.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new(await MapAsync(rows, cancellationToken), total, page, pageSize);
    }

    public async Task<DiscoveredDeviceResponse?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var row = await dbContext.DiscoveredDevices.AsNoTracking()
            .SingleOrDefaultAsync(device => device.Id == id, cancellationToken);
        return row is null ? null : (await MapAsync([row], cancellationToken))[0];
    }

    public async Task<DiscoveryReviewResult> ApproveAsync(
        Guid id,
        ApproveDiscoveredDeviceRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var row = await dbContext.DiscoveredDevices.SingleOrDefaultAsync(
            device => device.Id == id, cancellationToken);
        if (row is null)
        {
            return new(DiscoveryReviewOutcome.NotFound);
        }

        // Approving twice would create a second CI for one device, which is the duplicate the whole
        // package exists to prevent. A settled card is a 409 naming what it settled to.
        if (row.Status is DiscoveredDeviceStatus.Approved or DiscoveredDeviceStatus.Rejected)
        {
            return new(
                DiscoveryReviewOutcome.AlreadyReviewed,
                Error: $"This discovery was already {row.Status.ToString().ToLowerInvariant()} "
                    + $"by {row.ReviewedBy} on {row.ReviewedAt:u}.");
        }

        var before = Snapshot(row);
        Guid ciId;
        if (request.CiId is { } existingCiId)
        {
            // Settling an ambiguous match, or attaching to a CI the ladder never considered. No CI is
            // created; the human has told the platform which one this already is.
            if (!await CiExistsAsync(existingCiId, cancellationToken))
            {
                return new(DiscoveryReviewOutcome.Invalid, Errors: new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    [nameof(request.CiId)] = ["No CI with that id exists."],
                });
            }

            ciId = existingCiId;
        }
        else
        {
            var created = await CreateCiAsync(row, request, actor, cancellationToken);
            if (created.Outcome is not CiOutcome.Success)
            {
                // The CI service's own refusal, passed through rather than reworded: it names the
                // fields, and a second vocabulary for "asset tag already used" helps nobody.
                return new(
                    DiscoveryReviewOutcome.CiRejected,
                    Errors: created.Errors,
                    Error: created.Error ?? $"The CI could not be created ({created.Outcome}).");
            }

            ciId = created.Ci!.Id;
        }

        var now = DateTimeOffset.UtcNow;
        row.Status = DiscoveredDeviceStatus.Approved;
        row.CiId = ciId;
        row.MatchRule = request.CiId is null ? DiscoveryMatchRule.None : row.MatchRule;
        row.ReviewedBy = ActorName(actor);
        row.ReviewedAt = now;
        row.ReviewNote = Trim(request.Note);

        // The facts land on the CI immediately rather than waiting for the next sweep, so the card the
        // approver just cleared is reflected on the CI page they are about to open.
        await RefreshFactsFromRowAsync(ciId, row, cancellationToken);

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await publishEndpoint.Publish(
            new DiscoveredDeviceApproved(
                Guid.CreateVersion7(),
                now,
                row.Id,
                ciId,
                row.Address,
                row.Hostname,
                request.EnrollMonitoring,
                Trim(request.PollerGroup)),
            cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var response = (await MapAsync([row], cancellationToken))[0];
        await auditService.WriteAsync(
            actor, "Approved", "DiscoveredDevice", row.Id.ToString(), before, Snapshot(row), cancellationToken);
        return new(DiscoveryReviewOutcome.Success, response, ciId);
    }

    public async Task<DiscoveryReviewResult> RejectAsync(
        Guid id,
        RejectDiscoveredDeviceRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var row = await dbContext.DiscoveredDevices.SingleOrDefaultAsync(
            device => device.Id == id, cancellationToken);
        if (row is null)
        {
            return new(DiscoveryReviewOutcome.NotFound);
        }

        if (row.Status is DiscoveredDeviceStatus.Approved or DiscoveredDeviceStatus.Rejected)
        {
            return new(
                DiscoveryReviewOutcome.AlreadyReviewed,
                Error: $"This discovery was already {row.Status.ToString().ToLowerInvariant()} "
                    + $"by {row.ReviewedBy} on {row.ReviewedAt:u}.");
        }

        var before = Snapshot(row);
        row.Status = DiscoveredDeviceStatus.Rejected;

        // The CI link is cleared rather than kept. A rejected row is the ignore list and nothing else;
        // leaving a CI on it would make a later reader believe the platform still thinks they are the
        // same thing.
        row.CiId = null;
        row.MatchRule = DiscoveryMatchRule.None;
        row.ReviewedBy = ActorName(actor);
        row.ReviewedAt = DateTimeOffset.UtcNow;
        row.ReviewNote = Trim(request.Note);
        await dbContext.SaveChangesAsync(cancellationToken);

        var response = (await MapAsync([row], cancellationToken))[0];
        await auditService.WriteAsync(
            actor, "Rejected", "DiscoveredDevice", row.Id.ToString(), before, Snapshot(row), cancellationToken);
        return new(DiscoveryReviewOutcome.Success, response, null);
    }

    public async Task<CiDiscoveryFactsResponse?> GetFactsAsync(Guid ciId, CancellationToken cancellationToken)
    {
        var facts = await dbContext.CiDiscoveryFacts.AsNoTracking()
            .SingleOrDefaultAsync(item => item.CiId == ciId, cancellationToken);
        return facts is null ? null : new CiDiscoveryFactsResponse(
            facts.CiId,
            facts.Address,
            facts.Hostname,
            facts.RespondedToPing,
            Deserialise<int>(facts.OpenPortsJson),
            Snmp(facts.SysName, facts.SysDescription, facts.SysObjectId, facts.SysLocation, facts.SysContact, facts.UptimeSeconds),
            Deserialise<DiscoveredNeighbourResponse>(facts.NeighboursJson),
            facts.DiscoveryName,
            facts.ScanProfileName,
            facts.FirstSeenAt,
            facts.LastSeenAt,
            facts.SightingCount);
    }

    private async Task<CiResult> CreateCiAsync(
        DiscoveredDevice row,
        ApproveDiscoveredDeviceRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        var type = request.Type ?? SuggestedTypeFor(row);
        var attributes = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in SuggestedAttributesFor(row, type))
        {
            attributes[key] = value;
        }

        // The approver's values win over the scan's. Discovery only ever suggests an attribute it
        // actually observed, but the person looking at the card can see the device and it cannot.
        foreach (var (key, value) in request.Attributes ?? new Dictionary<string, string?>())
        {
            attributes[key] = value;
        }

        // Routed through ICiService rather than the DbContext, following WP-2.5's import rule: an
        // approved CI is validated, CiTypeSchema-bound, audited and published exactly like one typed
        // into the form. Nothing about arriving from a scan makes it a lesser record.
        return await ciService.CreateAsync(
            new CreateCiRequest(
                type,
                Trim(request.Name) ?? SuggestedNameFor(row),
                Trim(request.AssetTag),
                Trim(request.SerialNumber),
                Trim(request.Description) ?? row.SysDescription,
                attributes),
            actor,
            cancellationToken);
    }

    private async Task RefreshFactsFromRowAsync(Guid ciId, DiscoveredDevice row, CancellationToken cancellationToken)
    {
        var facts = await dbContext.CiDiscoveryFacts.SingleOrDefaultAsync(
            item => item.CiId == ciId, cancellationToken);
        if (facts is null)
        {
            facts = new CiDiscoveryFacts
            {
                CiId = ciId,
                Address = row.Address,
                OpenPortsJson = row.OpenPortsJson,
                NeighboursJson = row.NeighboursJson,
                DiscoveryName = row.DiscoveryName,
                ScanProfileName = row.ScanProfileName,
                FirstSeenAt = row.FirstSeenAt,
                SightingCount = 0,
            };
            dbContext.CiDiscoveryFacts.Add(facts);
        }

        facts.Address = row.Address;
        facts.Hostname = row.Hostname;
        facts.RespondedToPing = row.RespondedToPing;
        facts.OpenPortsJson = row.OpenPortsJson;
        facts.SysName = row.SysName;
        facts.SysDescription = row.SysDescription;
        facts.SysObjectId = row.SysObjectId;
        facts.SysLocation = row.SysLocation;
        facts.SysContact = row.SysContact;
        facts.UptimeSeconds = row.UptimeSeconds;
        facts.NeighboursJson = row.NeighboursJson;
        facts.DiscoveryName = row.DiscoveryName;
        facts.ScanProfileName = row.ScanProfileName;
        facts.LastScanId = row.LastScanId;
        facts.LastSeenAt = row.LastSeenAt;
        facts.SightingCount = Math.Max(facts.SightingCount, row.SightingCount);
    }

    private static DiscoveredDevice NewRow(DeviceDiscovered discovery, DiscoveryFingerprint fingerprint) => new()
    {
        Id = Guid.CreateVersion7(),
        IdentityKey = DiscoveryIdentity.KeyFor(fingerprint),
        Address = discovery.Address,
        OpenPortsJson = "[]",
        NeighboursJson = "[]",
        ContenderCiIdsJson = "[]",
        DiscoveryName = discovery.DiscoveryName,
        ScanProfileName = discovery.ScanProfileName,
        FirstSeenAt = discovery.OccurredAt,
        SightingCount = 0,
    };

    /// <summary>Writes what this sighting saw over the row, whatever the row's decision turns out to be.</summary>
    private static void Observe(
        DiscoveredDevice row,
        DeviceDiscovered discovery,
        DiscoveryFingerprint fingerprint,
        bool isNew)
    {
        row.IdentityKey = DiscoveryIdentity.KeyFor(fingerprint);
        row.Address = discovery.Address;
        row.Hostname = discovery.Hostname;
        row.HostnameSource = discovery.HostnameSource;
        row.RespondedToPing = discovery.RespondedToPing;
        row.OpenPortsJson = Serialise(discovery.OpenPorts);
        row.SysName = discovery.Snmp?.SysName;
        row.SysDescription = discovery.Snmp?.SysDescription;
        row.SysObjectId = discovery.Snmp?.SysObjectId;
        row.SysLocation = discovery.Snmp?.SysLocation;
        row.SysContact = discovery.Snmp?.SysContact;
        row.UptimeSeconds = discovery.Snmp?.UptimeSeconds;
        row.NeighboursJson = Serialise(discovery.Neighbours);
        row.DiscoveryName = discovery.DiscoveryName;
        row.ScanProfileId = discovery.ScanProfileId;
        row.ScanProfileName = discovery.ScanProfileName;
        row.LastScanId = discovery.ScanId;

        // Out-of-order delivery is ordinary on a bus, so last-seen only ever moves forward.
        row.LastSeenAt = isNew || discovery.OccurredAt > row.LastSeenAt ? discovery.OccurredAt : row.LastSeenAt;
        row.FirstSeenAt = isNew || discovery.OccurredAt < row.FirstSeenAt ? discovery.OccurredAt : row.FirstSeenAt;
        row.SightingCount++;
    }

    private Task<bool> CiExistsAsync(Guid ciId, CancellationToken cancellationToken) =>
        dbContext.Cis.AsNoTracking().AnyAsync(ci => ci.Id == ciId, cancellationToken);

    private async Task<IReadOnlyList<DiscoveredDeviceResponse>> MapAsync(
        IReadOnlyList<DiscoveredDevice> rows,
        CancellationToken cancellationToken)
    {
        // Every CI any of these rows names, in one query: the matched ones and every contender, so the
        // cards can be rendered with names rather than ids.
        var wanted = rows.Select(row => row.CiId).OfType<Guid>()
            .Concat(rows.SelectMany(row => Deserialise<Guid>(row.ContenderCiIdsJson)))
            .Distinct()
            .ToArray();
        var names = wanted.Length == 0
            ? []
            : await dbContext.Cis.AsNoTracking()
                .Where(ci => wanted.Contains(ci.Id))
                .Select(ci => new { ci.Id, ci.Name, ci.Type })
                .ToDictionaryAsync(ci => ci.Id, ci => (ci.Name, ci.Type), cancellationToken);

        return
        [
            .. rows.Select(row => new DiscoveredDeviceResponse(
                row.Id,
                row.IdentityKey,
                row.Address,
                row.Hostname,
                row.HostnameSource,
                row.RespondedToPing,
                Deserialise<int>(row.OpenPortsJson),
                Snmp(row.SysName, row.SysDescription, row.SysObjectId, row.SysLocation, row.SysContact, row.UptimeSeconds),
                Deserialise<DiscoveredNeighbourResponse>(row.NeighboursJson),
                row.DiscoveryName,
                row.ScanProfileId,
                row.ScanProfileName,
                row.Status,
                row.CiId,
                row.CiId is { } ciId && names.TryGetValue(ciId, out var matched) ? matched.Name : null,
                row.MatchRule,
                [
                    .. Deserialise<Guid>(row.ContenderCiIdsJson)
                        .Where(names.ContainsKey)
                        .Select(id => new DiscoveryContenderResponse(id, names[id].Name, names[id].Type))
                ],
                SuggestedTypeFor(row),
                SuggestedNameFor(row),
                SuggestedAttributesFor(row, SuggestedTypeFor(row)),
                row.FirstSeenAt,
                row.LastSeenAt,
                row.SightingCount,
                row.ReviewedBy,
                row.ReviewedAt,
                row.ReviewNote))
        ];
    }

    /// <summary>
    /// What the card offers as a starting type.
    /// <para>
    /// Only one inference here is safe enough to make: a device that reported LLDP or CDP neighbours is
    /// network equipment, because nothing else runs those protocols. Everything else falls to
    /// <c>Hardware</c> — the type that claims least — rather than guessing a server from an open port
    /// 22, which every switch in the estate also answers.
    /// </para>
    /// </summary>
    internal static CiType SuggestedTypeFor(DiscoveredDevice row) =>
        Deserialise<DiscoveredNeighbourResponse>(row.NeighboursJson).Count > 0
            ? CiType.NetworkDevice
            : CiType.Hardware;

    internal static string SuggestedNameFor(DiscoveredDevice row) =>
        DiscoveryIdentity.Normalise(row.SysName)
        ?? DiscoveryIdentity.ShortHostname(row.Hostname)
        ?? row.Address;

    /// <summary>
    /// The attributes discovery can fill in for a type, and only those. A scan observes an address and
    /// a name; it does not observe a vendor, a port count or an amount of RAM, and inventing them would
    /// make the CMDB's required fields meaningless. What is missing is what the approver types.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> SuggestedAttributesFor(DiscoveredDevice row, CiType type)
    {
        var suggestions = new Dictionary<string, string>(StringComparer.Ordinal);
        switch (type)
        {
            case CiType.NetworkDevice:
                suggestions["managementIp"] = row.Address;
                break;
            case CiType.Server:
            case CiType.Virtual:
                if (SuggestedNameFor(row) is { } hostname && hostname != row.Address)
                {
                    suggestions["hostname"] = hostname;
                }

                break;
            default:
                break;
        }

        return suggestions;
    }

    private static DiscoveredSnmpResponse? Snmp(
        string? sysName,
        string? sysDescription,
        string? sysObjectId,
        string? sysLocation,
        string? sysContact,
        double? uptimeSeconds) =>
        sysName is null && sysDescription is null && sysObjectId is null
        && sysLocation is null && sysContact is null && uptimeSeconds is null
            ? null
            : new DiscoveredSnmpResponse(sysName, sysDescription, sysObjectId, sysLocation, sysContact, uptimeSeconds);

    private static object Snapshot(DiscoveredDevice row) => new
    {
        row.IdentityKey,
        row.Address,
        row.Hostname,
        row.SysName,
        Status = row.Status.ToString(),
        row.CiId,
        MatchRule = row.MatchRule.ToString(),
        row.SightingCount,
        row.ReviewedBy,
        row.ReviewedAt,
        row.ReviewNote,
    };

    private static string ActorName(ClaimsPrincipal actor) =>
        actor.Identity?.Name
        ?? actor.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
        ?? "unknown";

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Serialise<T>(IEnumerable<T> values) => JsonSerializer.Serialize(values, Json);

    private static IReadOnlyList<T> Deserialise<T>(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<T>>(json, Json) ?? [];
        }
        catch (JsonException)
        {
            // A column this module wrote should always parse, so this is defence rather than a path:
            // a card that renders without its neighbour list is better than a review queue that 500s.
            return [];
        }
    }
}
