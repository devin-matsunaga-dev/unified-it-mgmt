namespace Modules.Monitoring.Features.Alerting;

/// <summary>
/// Platform-wide alert tuning, bound from <c>Monitoring:Alerting</c>. Every value here is a default
/// a check may override — see <see cref="AlertPolicy.Resolve"/> — so this section answers "what
/// happens to a check nobody has tuned", which is most of them.
/// </summary>
public sealed class AlertOptions
{
    public const string SectionName = "Monitoring:Alerting";

    /// <summary>
    /// Three: one bad reading is a dropped packet or a busy moment, and two is not yet a pattern. On
    /// a fifteen-second check this is under a minute; on a five-minute check it is a quarter of an
    /// hour, which is why the value is per check and not per unit of time.
    /// </summary>
    public int SustainedCycles { get; set; } = 3;

    /// <summary>
    /// Two. Recovery is deliberately quicker to believe than failure: an alert that outlives the
    /// problem is the thing that makes people stop reading alerts.
    /// </summary>
    public int RecoveryCycles { get; set; } = 2;

    /// <summary>
    /// Five percent of the threshold. A CPU warning at 70% is not considered recovered until the
    /// reading falls to 66.5, so a device sitting at exactly 70 does not alternate every cycle.
    /// </summary>
    public double HysteresisPercent { get; set; } = 5;

    /// <summary>
    /// Four state changes inside <see cref="FlapWindowSeconds"/>. Two raise/clear pairs is a rule
    /// that has said "broken" and "fine" twice each within ten minutes.
    /// </summary>
    public int FlapThreshold { get; set; } = 4;

    public int FlapWindowSeconds { get; set; } = 600;

    /// <summary>
    /// How long a flapping rule stays quiet. Equal to the window by default, so a rule has to be
    /// stable for as long as it was unstable before it can speak again.
    /// </summary>
    public int FlapCooldownSeconds { get; set; } = 600;

    /// <summary>
    /// How long a rule's Redis state outlives its last reading. A week: long enough that a poller
    /// outage over a long weekend does not reset every counter, short enough that a check deleted a
    /// year ago is not still holding a key.
    /// </summary>
    public int StateTtlDays { get; set; } = 7;
}
