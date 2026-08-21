namespace Modules.Assets.Data;

/// <summary>
/// How far ahead of an expiry the platform writes to the contract's owner.
/// <para>
/// A single row rather than configuration, for the same reason the discovery switch is: somebody has
/// to be able to change it without a restart, and the answer to "why did this notice arrive" should
/// be readable from the database rather than inferred from a deployment.
/// </para>
/// <para>
/// This governs the <em>notices</em> only. Whether a contract shows as "expiring soon" in a list is a
/// separate 30-day rule in <c>ContractExpiryCalculator.Status</c>, deliberately left alone: it colours
/// pills across contracts, CI coverage and the dashboard, and moving it would quietly restate what
/// every one of those screens means.
/// </para>
/// </summary>
public sealed class ContractReminderSettings
{
    /// <summary>
    /// Fixed, so the row is a singleton by primary key rather than by convention — nothing allocates
    /// an id for it and a second row cannot be inserted by accident.
    /// </summary>
    public static readonly Guid SingletonId = Guid.Parse("0199c0de-4155-7000-8000-000000000002");

    /// <summary>What the platform used before this was configurable: a month out, a week out, the day.</summary>
    public static readonly int[] DefaultThresholdDays = [30, 7, 0];

    public Guid Id { get; set; } = SingletonId;

    /// <summary>
    /// Days before expiry at which a notice is sent, one per entry. Stored in days rather than months
    /// because that is what a notice is keyed on — <c>ContractNotification.ThresholdDays</c> — and a
    /// month is not a fixed number of days, so storing "2 months" would mean choosing what that meant
    /// on every read instead of once when it was set.
    /// </summary>
    public int[] ThresholdDays { get; set; } = DefaultThresholdDays;

    /// <summary>
    /// Off silences renewal notices without losing the thresholds, so a switch-off is reversible and
    /// does not require somebody to remember what the numbers were.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Who hears about every contract renewal — the team that actually does them, rather than
    /// whoever happens to be recorded as a contract's internal owner.
    /// <para>
    /// Empty falls back to the owner, then to the configured asset mailbox, which is what the job did
    /// before this existed. Warranty and licence notices are unaffected: those are about one asset and
    /// belong with whoever holds it.
    /// </para>
    /// </summary>
    public string[] Recipients { get; set; } = [];

    public required string UpdatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
