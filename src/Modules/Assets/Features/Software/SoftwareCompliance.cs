using Modules.Assets.Features.Contracts;

namespace Modules.Assets.Features.Software;

/// <summary>
/// What one product's pools and installs add up to, before any of it is rendered.
/// <para>
/// <paramref name="PoolCount"/> and <paramref name="LivePoolCount"/> are both here because they answer
/// different questions: whether anybody ever bought this product, and whether what they bought still
/// entitles anything today. A product whose only licence has lapsed is over-deployed, not unlicensed.
/// </para>
/// </summary>
public sealed record SoftwareComplianceTally(int InstalledCiCount, int PoolCount, int LivePoolCount, int Entitled)
{
    /// <summary>Positive when more devices carry the product than the live pools entitle.</summary>
    public int Overage => InstalledCiCount - Entitled;
}

/// <summary>
/// The one place installs and entitlements turn into a compliance state. Pure, so the whole matrix —
/// including the two states that are easy to get backwards — is testable without a database.
/// <para>
/// Entitlements are summed over a product's <em>live</em> pools: active, and either perpetual or not yet
/// past their end date. An expired licence entitles nothing, so a lapse turns a compliant product
/// over-deployed on the day it lapses. That is the point of the expiry notices rather than a side effect
/// of them.
/// </para>
/// </summary>
public static class SoftwareComplianceCalculator
{
    public static SoftwareComplianceState State(SoftwareComplianceTally tally)
    {
        ArgumentNullException.ThrowIfNull(tally);

        // Never bought, but installed: nobody has recorded an entitlement for this at all. Distinct
        // from over-deployment, which is a shortfall against a licence somebody actually holds — and
        // from a lapsed one, which falls through to over-deployment with an entitlement of zero.
        if (tally.PoolCount == 0)
        {
            return tally.InstalledCiCount > 0 ? SoftwareComplianceState.Unlicensed : SoftwareComplianceState.Compliant;
        }

        if (tally.InstalledCiCount == 0)
        {
            // Entitlements nobody is using — unless they have all expired, in which case there is
            // nothing left to have wasted.
            return tally.LivePoolCount > 0 ? SoftwareComplianceState.Unused : SoftwareComplianceState.Compliant;
        }

        return tally.Overage > 0 ? SoftwareComplianceState.OverDeployed : SoftwareComplianceState.Compliant;
    }

    /// <summary>Whether a pool still entitles anything today.</summary>
    public static bool IsLive(bool isActive, DateOnly? expiresAt, DateOnly today) =>
        isActive && (expiresAt is null || expiresAt.Value >= today);

    /// <summary>A pool's expiry status, or null when it is perpetual and has no clock to be on.</summary>
    public static ContractExpiryStatus? Status(DateOnly? expiresAt, DateOnly today) =>
        expiresAt is { } dueDate ? ContractExpiryCalculator.Status(dueDate, today) : null;
}
