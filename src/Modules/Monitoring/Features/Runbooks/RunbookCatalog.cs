using System.Text.RegularExpressions;

namespace Modules.Monitoring.Features.Runbooks;

/// <summary>
/// One thing a runbook may be told, and the only shape it may take.
/// </summary>
/// <param name="Pattern">
/// What a value is allowed to look like, anchored and compiled once. It is a security control rather
/// than a tidiness one: this string is handed to an agent that turns it into an argument, so a pattern
/// that admitted a space, a semicolon or a newline would be the beginning of the free-text execution
/// path ARCHITECTURE §7 invariant 4 says does not exist. Every pattern here is a deny-by-default
/// character class — never a "reject the bad ones" list.
/// </param>
public sealed record RunbookParameter(
    string Name,
    string Description,
    bool IsRequired,
    int MaxLength,
    Regex Pattern,
    string Example);

/// <summary>
/// One allowlisted runbook, as the server knows it.
/// <para>
/// There is deliberately no field here for a script, a command line, a path or an interpreter. The
/// agent holds the implementation and is given a key and validated parameters; this record is the
/// contract between the two, and its shape is what makes "no free-text execution path exists anywhere"
/// a property of the type system rather than a rule somebody has to keep remembering.
/// </para>
/// </summary>
public sealed record RunbookDefinition(
    string Key,
    string Name,
    string Description,
    int DefaultTimeoutSeconds,
    IReadOnlyList<RunbookParameter> Parameters);

/// <summary>
/// The allowlist. Compiled into the server, closed, and the only source of truth for what may ever be
/// asked of an agent.
/// <para>
/// Being code rather than a table is the point. A table of runbooks is a table somebody can insert
/// into — through the API, through a migration, through a restored backup, through SQL — and every one
/// of those becomes a way to make the platform run something new. <c>monitoring.runbooks</c> holds
/// registrations of keys that appear here; a row naming a key that does not is refused at every point
/// it could otherwise be acted on, which is what the 403 in the WP's verification list is.
/// </para>
/// <para>
/// Adding a runbook is therefore a code change reviewed like one, in two places that must agree: an
/// entry here and an implementation in the poller's own registry. They cannot be checked against each
/// other at build time — one is C# and the other Python — so the disagreement is made loud instead: an
/// agent asked for a key it does not implement refuses it, the execution is recorded as failed, and it
/// escalates like any other failure. A silent no-op was the alternative and is much worse.
/// </para>
/// </summary>
public static class RunbookCatalog
{
    /// <summary>
    /// A service unit name. Letters, digits and the four punctuation marks systemd itself allows, and
    /// nothing else — no space, no slash, no shell metacharacter, no newline.
    /// </summary>
    private static readonly Regex ServiceUnitPattern = new(
        "^[A-Za-z0-9][A-Za-z0-9._@-]{0,63}$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        TimeSpan.FromMilliseconds(100));

    public const string RestartService = "restart-service";

    private static readonly IReadOnlyDictionary<string, RunbookDefinition> Definitions =
        new Dictionary<string, RunbookDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            [RestartService] = new(
                RestartService,
                "Restart a service",
                "Restarts one named service on the host the poller runs on. The poller builds the "
                    + "command from a template it holds; nothing about the command travels from here.",
                DefaultTimeoutSeconds: 60,
                Parameters:
                [
                    new RunbookParameter(
                        "service",
                        "The service unit to restart, as the host names it.",
                        IsRequired: true,
                        MaxLength: 64,
                        ServiceUnitPattern,
                        Example: "nginx"),
                ]),
        };

    /// <summary>Every allowlisted runbook, ordered by key so a list read is stable.</summary>
    public static IReadOnlyList<RunbookDefinition> All { get; } =
        [.. Definitions.Values.OrderBy(definition => definition.Key, StringComparer.Ordinal)];

    /// <summary>
    /// The definition for a key, or null. Callers must treat null as "refuse", never as "assume a
    /// default" — an unknown key is the case this whole file exists to stop.
    /// </summary>
    public static RunbookDefinition? Find(string? key) =>
        key is not null && Definitions.TryGetValue(key.Trim(), out var definition) ? definition : null;

    public static bool Contains(string? key) => Find(key) is not null;

    /// <summary>The key as the catalogue spells it, so a registration cannot differ from it by case.</summary>
    public static string? Canonicalise(string? key) => Find(key)?.Key;
}
