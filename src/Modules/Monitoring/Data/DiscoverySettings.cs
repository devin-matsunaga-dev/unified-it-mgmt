namespace Modules.Monitoring.Data;

/// <summary>
/// The estate-wide discovery switches — one row, always.
/// <para>
/// A singleton table rather than an options section, because the point of it is that somebody can
/// change it while the stack is running. <c>Monitoring:Discovery</c> in configuration would need a
/// restart of the host to take effect, which is exactly what a kill switch must not need.
/// </para>
/// <para>
/// Only what is genuinely estate-wide lives here. A profile's interval and its own schedule toggle stay
/// on the profile, because they are properties of that range rather than of discovery as a whole; this
/// is the one switch that answers "stop scanning the network" without editing anything.
/// </para>
/// </summary>
public sealed class DiscoverySettings
{
    /// <summary>
    /// Fixed, so the row is a singleton by primary key rather than by convention. Nothing allocates an
    /// id for these and a second row cannot be inserted by accident.
    /// </summary>
    public static readonly Guid SingletonId = Guid.Parse("0199c0de-4155-7000-8000-000000000001");

    public Guid Id { get; set; } = SingletonId;

    /// <summary>
    /// Whether profiles run on their own intervals at all. Off stops every scheduled sweep in every
    /// group and leaves on-demand runs working — the switch is aimed at the clock, not at the scanner,
    /// so an operator can still ask for a specific range while the estate is otherwise left alone.
    /// </summary>
    public bool ScheduledScanningEnabled { get; set; } = true;

    public required string UpdatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
