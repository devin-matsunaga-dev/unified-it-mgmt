namespace Modules.Monitoring.Features.Discovery;

/// <summary>
/// The bounds on requested scans. Every one has a default, so an unconfigured deployment is still
/// bounded — WP-5.6's rule for runbook options, applied to the second agent channel in this solution.
/// <para>
/// The estate-wide on/off switch is deliberately <em>not</em> here. It lives in
/// <see cref="Data.DiscoverySettings"/>, because somebody has to be able to throw it without a restart
/// of the host; what stays in configuration is the shape of the channel rather than the decision.
/// </para>
/// </summary>
public sealed class DiscoveryOptions
{
    public const string SectionName = "Monitoring:Discovery";

    /// <summary>How many queued runs one scanner fetch may claim. A batch, not a queue drain.</summary>
    public int DispatchBatchSize { get; set; } = 3;

    /// <summary>
    /// How long a claimed run may take before the platform stops waiting for it. Generous next to a
    /// runbook's sixty seconds, because a sweep is not a command: a /24 with a port list is more than a
    /// thousand probes, and the scanner runs profiles one at a time, so a run can legitimately sit
    /// behind another one for minutes.
    /// </summary>
    public int RunTimeoutMinutes { get; set; } = 30;

    /// <summary>How often the sweeper looks for runs no scanner ever reported on.</summary>
    public int SweepIntervalSeconds { get; set; } = 60;
}
