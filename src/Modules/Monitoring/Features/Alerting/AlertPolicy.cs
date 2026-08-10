using Modules.Monitoring.Data;

namespace Modules.Monitoring.Features.Alerting;

/// <summary>
/// The tuning one rule is evaluated under: how long a problem has to last, how far it has to come
/// back, and how often it may change its mind before nobody wants to hear about it any more.
/// <para>
/// Resolved per check from the platform defaults in <see cref="AlertOptions"/> overridden by the
/// nullable columns on <see cref="CheckDefinition"/>. A value is either configured for the check or
/// it is the platform's — there is no third source, so a surprising evaluation has exactly two
/// places to look.
/// </para>
/// </summary>
/// <param name="SustainedCycles">
/// How many consecutive readings have to agree before a rule gets worse. The WP's "for N cycles"
/// condition, and the whole of why crossing a threshold once is not an alert.
/// </param>
/// <param name="RecoveryCycles">
/// How many consecutive good readings clear an alert. The Recovering state is exactly the interval
/// between the first of them and the last.
/// </param>
/// <param name="HysteresisPercent">
/// How far back past a threshold a value must come before the rule counts as having improved,
/// expressed as a percentage of the threshold. Zero disables it, which makes a value sitting exactly
/// on a threshold alternate every cycle — which is what the flap policy would then have to catch.
/// </param>
/// <param name="FlapThreshold">
/// How many raise/clear flips inside <paramref name="FlapWindow"/> mean the rule is flapping rather
/// than reporting. Counted on the rule's own state changes, not on published messages, so
/// suppression cannot hide the evidence that suppression is needed.
/// </param>
/// <param name="FlapWindow">The period the flips are counted over.</param>
/// <param name="FlapCooldown">
/// How long a rule stays suppressed after it is judged to be flapping. When it expires, the rule
/// reconciles: if it is still bad and nobody was told, it publishes then.
/// </param>
public sealed record AlertPolicy(
    int SustainedCycles,
    int RecoveryCycles,
    double HysteresisPercent,
    int FlapThreshold,
    TimeSpan FlapWindow,
    TimeSpan FlapCooldown)
{
    /// <summary>The platform's defaults, with any per-check override applied on top.</summary>
    public static AlertPolicy Resolve(AlertOptions options, CheckDefinition? check)
    {
        ArgumentNullException.ThrowIfNull(options);

        return new AlertPolicy(
            SustainedCycles: check?.SustainedCycles ?? options.SustainedCycles,
            RecoveryCycles: check?.RecoveryCycles ?? options.RecoveryCycles,
            HysteresisPercent: check?.HysteresisPercent ?? options.HysteresisPercent,
            FlapThreshold: check?.FlapThreshold ?? options.FlapThreshold,
            FlapWindow: TimeSpan.FromSeconds(check?.FlapWindowSeconds ?? options.FlapWindowSeconds),
            FlapCooldown: TimeSpan.FromSeconds(options.FlapCooldownSeconds));
    }
}
