using System.Text.RegularExpressions;

namespace Modules.Helpdesk.Features.CannedResponses;

/// <summary>
/// Substitutes <c>{{group.field}}</c> placeholders in a canned response body. Tokens outside the supported
/// set are left exactly as written so a typo is visible to the agent instead of silently emptying the reply.
/// </summary>
public static partial class CannedResponseRenderer
{
    public static readonly string[] SupportedPlaceholders =
        ["ticket.id", "ticket.number", "ticket.title", "requester.name", "agent.name"];

    public static string Render(string body, IReadOnlyDictionary<string, string> values) =>
        PlaceholderPattern().Replace(
            body,
            match => values.TryGetValue(match.Groups[1].Value.ToLowerInvariant(), out var value)
                ? value
                : match.Value);

    [GeneratedRegex(@"\{\{\s*([A-Za-z]+\.[A-Za-z]+)\s*\}\}")]
    private static partial Regex PlaceholderPattern();
}
