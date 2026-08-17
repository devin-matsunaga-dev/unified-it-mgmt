using Microsoft.EntityFrameworkCore;

using Modules.Assets.Data;

namespace Modules.Assets.Seeding;

public sealed record ChangeRequestSeedResult(int ChangesAdded, Guid? ChangeId, Guid? CiId);

/// <summary>
/// One draft change on the switch the Phase 3 demo can stop, so WP-5.8's own verification step — approve
/// maintenance on the sim device, stop it, see no alerts — is walkable a minute after <c>aspire run</c>
/// with one field to edit and two buttons to press.
/// <para>
/// A draft rather than an approved change, deliberately: approving it is the act being demonstrated, and
/// a seeder that had already done it would leave nothing to watch. It is also why the seeded requester is
/// <c>seeder</c> and not a real username — the workflow refuses to let anybody approve their own change,
/// so a draft raised by a person would be one the person who found it cannot action.
/// </para>
/// <para>
/// Its planned window is an offset from the moment the seeder runs rather than a fixed date, following
/// WP-2.8: the dev database is recreated on most AppHost restarts and fixed dates drift into the past,
/// where <see cref="Features.Changes.ChangeWorkflow"/> would refuse the approval outright.
/// </para>
/// </summary>
public sealed class ChangeRequestSeeder(AssetsDbContext dbContext)
{
    /// <summary>The CI the WP-3.12 down-able simulator container is, and the one WP-5.8 asks to mute.</summary>
    public const string CiKey = "dc1-acc-sw-01";

    /// <summary>
    /// Two hours, which is long enough that the draft is still approvable however long somebody takes to
    /// reach it, and short enough to be an honest maintenance slot. Watching a window expire means
    /// shortening it first, which is a single field on a draft and exercises the edit path anyway.
    /// </summary>
    private static readonly TimeSpan PlannedDuration = TimeSpan.FromHours(2);

    private static readonly Guid ChangeId = Guid.Parse("01980000-0000-7000-8000-000000000581");

    private const string Title = "Firmware upgrade on the DC1 access switch";

    private const string Description =
        "Vendor firmware 12.4(3) applied to the DC1 access switch. The switch reboots twice during the "
        + "upgrade and is unreachable for roughly ten minutes each time, so monitoring should be muted "
        + "for the slot rather than paging out of hours.";

    /// <param name="ciIds">Every seeded CI, keyed as <see cref="AssetsEstate"/> names them.</param>
    public async Task<ChangeRequestSeedResult> SeedAsync(
        IReadOnlyDictionary<string, Guid> ciIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ciIds);
        if (!ciIds.TryGetValue(CiKey, out var ciId))
        {
            return new ChangeRequestSeedResult(0, null, null);
        }

        if (await dbContext.ChangeRequests.AnyAsync(change => change.Id == ChangeId, cancellationToken))
        {
            return new ChangeRequestSeedResult(0, ChangeId, ciId);
        }

        var now = DateTimeOffset.UtcNow;
        dbContext.ChangeRequests.Add(new ChangeRequest
        {
            Id = ChangeId,
            Title = Title,
            Description = Description,
            Status = ChangeRequestStatus.Draft,
            PlannedStartAt = now,
            PlannedEndAt = now + PlannedDuration,
            // Off, so the seeded change mutes exactly the one device the checklist stops. Ticking it is
            // a step of its own — the switch has a backup server and a laptop hanging off it, so the
            // expansion has something true to find.
            IncludeDependents = false,
            RequestedById = "seeder",
            RequestedByName = "Demo seeder",
            RequestedAt = now,
            UpdatedAt = now,
            Cis = [new ChangeRequestCi { ChangeRequestId = ChangeId, CiId = ciId, IsDependent = false }],
        });
        await dbContext.SaveChangesAsync(cancellationToken);

        return new ChangeRequestSeedResult(1, ChangeId, ciId);
    }
}
