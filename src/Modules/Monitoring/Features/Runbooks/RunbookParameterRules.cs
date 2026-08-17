using System.Text.RegularExpressions;

namespace Modules.Monitoring.Features.Runbooks;

/// <summary>
/// Binding a caller's parameters to a runbook's schema, and refusing everything that does not fit.
/// <para>
/// Pure, so the whole matrix is unit-testable without a database — the same split
/// <c>AlertTicketPolicy</c> and <c>ScanProfileRules</c> make. It is also the single place a value ever
/// becomes eligible to reach an agent, so it fails closed in every direction: an unknown name is an
/// error rather than a dropped key, a blank required value is an error rather than a default, and a
/// value that does not match its pattern is an error rather than an escaped one. Nothing here
/// sanitises; sanitising is how a rejected input becomes an accepted one.
/// </para>
/// </summary>
public static class RunbookParameterRules
{
    /// <summary>
    /// Validates and normalises. On success the returned dictionary contains exactly the declared
    /// names, trimmed — never the caller's dictionary, so a key that was refused cannot survive by
    /// reference.
    /// </summary>
    public static RunbookParameterBinding Bind(
        RunbookDefinition definition,
        IReadOnlyDictionary<string, string>? parameters)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var bound = new Dictionary<string, string>(StringComparer.Ordinal);
        var supplied = parameters ?? new Dictionary<string, string>(StringComparer.Ordinal);

        // Unknown names first, and named individually. A caller who misspells `service` has supplied
        // nothing and omitted everything, and being told both halves is what makes that obvious.
        foreach (var name in supplied.Keys.Order(StringComparer.Ordinal))
        {
            if (!definition.Parameters.Any(parameter =>
                    string.Equals(parameter.Name, name, StringComparison.Ordinal)))
            {
                errors[$"parameters.{name}"] =
                    [$"'{definition.Key}' takes no parameter called '{name}'."];
            }
        }

        foreach (var parameter in definition.Parameters)
        {
            var field = $"parameters.{parameter.Name}";
            supplied.TryGetValue(parameter.Name, out var raw);
            var value = raw?.Trim();

            if (string.IsNullOrEmpty(value))
            {
                if (parameter.IsRequired)
                {
                    errors[field] = [$"'{parameter.Name}' is required. {parameter.Description}"];
                }

                // An optional parameter that was not supplied is absent, not empty. The agent
                // distinguishes them, and an empty string is a value somebody has to decide about.
                continue;
            }

            if (value.Length > parameter.MaxLength)
            {
                errors[field] =
                    [$"'{parameter.Name}' may be at most {parameter.MaxLength} characters."];
                continue;
            }

            if (!Matches(parameter.Pattern, value))
            {
                // The pattern is not quoted back. It is a security control, and echoing it turns a
                // refusal into instructions for writing something that gets through.
                errors[field] =
                    [$"'{parameter.Name}' is not in the form this runbook accepts (for example '{parameter.Example}')."];
                continue;
            }

            bound[parameter.Name] = value;
        }

        return errors.Count > 0
            ? new RunbookParameterBinding(null, errors)
            : new RunbookParameterBinding(bound, null);
    }

    /// <summary>
    /// A pattern that takes too long to decide is a refusal. It cannot happen with the catalogue's
    /// current patterns — they are non-backtracking — but a regex timeout that threw here would fail an
    /// execution request with a 500 instead of a "no", and a validator whose failure mode is louder
    /// than its rejection is one people work around.
    /// </summary>
    private static bool Matches(Regex pattern, string value)
    {
        try
        {
            return pattern.IsMatch(value);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }
}

/// <param name="Values">The bound parameters, or null when <paramref name="Errors"/> is set.</param>
public sealed record RunbookParameterBinding(
    IReadOnlyDictionary<string, string>? Values,
    IReadOnlyDictionary<string, string[]>? Errors)
{
    public bool IsValid => Errors is null;
}
