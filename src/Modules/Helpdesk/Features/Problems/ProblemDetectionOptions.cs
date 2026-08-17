namespace Modules.Helpdesk.Features.Problems;

/// <summary>
/// What counts as a recurrence, and how loudly the platform is allowed to say so.
/// <para>
/// Configuration rather than constants because "five in a week" is a claim about one service desk's
/// traffic and not about IT — a team fielding thirty incidents a day and a team fielding thirty a month
/// want very different numbers, and neither wants to edit the source to say so.
/// </para>
/// </summary>
public sealed class ProblemDetectionOptions
{
    public const string SectionName = "Helpdesk:ProblemDetection";

    /// <summary>
    /// Switches the nightly pass off. The suggestions already raised stay readable and answerable — this
    /// stops new ones being written, it does not hide what somebody has yet to look at.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Incidents on one subject inside the window before it is worth asking about. The WP's "≥N".</summary>
    public int MinimumIncidents { get; set; } = 5;

    /// <summary>How far back the pass counts.</summary>
    public int WindowDays { get; set; } = 7;

    /// <summary>
    /// How long a dismissal suppresses the same subject. Defaults to the window, so a dismissal survives
    /// exactly as long as the evidence that produced it.
    /// </summary>
    public int DismissalCooldownDays { get; set; } = 7;

    /// <summary>
    /// The most suggestions one pass may raise. A bound in the spirit of ARCHITECTURE §7 invariant 4: the
    /// first pass on an estate with years of history behind it would otherwise fill the inbox with more
    /// than anybody will read, and an inbox nobody reads is the same as no inbox.
    /// </summary>
    public int MaxSuggestionsPerRun { get; set; } = 25;
}
