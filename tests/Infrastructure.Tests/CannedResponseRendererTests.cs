using Modules.Helpdesk.Features.CannedResponses;

namespace Infrastructure.Tests;

public sealed class CannedResponseRendererTests
{
    private static readonly Dictionary<string, string> Values = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ticket.id"] = "0198a0b1-0000-7000-8000-000000000001",
        ["ticket.number"] = "INC-000042",
        ["ticket.title"] = "Printer offline",
        ["requester.name"] = "Ada Lovelace",
        ["agent.name"] = "Technician One",
    };

    [Fact]
    public void Render_SupportedPlaceholders_AreSubstituted()
    {
        var body = Render("Hi {{requester.name}}, {{ticket.number}} (\"{{ticket.title}}\") is with {{agent.name}}.");

        Assert.Equal("Hi Ada Lovelace, INC-000042 (\"Printer offline\") is with Technician One.", body);
    }

    [Fact]
    public void Render_PlaceholderWithSurroundingWhitespaceOrCasing_IsStillSubstituted()
    {
        var body = Render("{{ ticket.number }} and {{Requester.Name}}");

        Assert.Equal("INC-000042 and Ada Lovelace", body);
    }

    [Fact]
    public void Render_UnknownPlaceholder_IsLeftLiteral()
    {
        var body = Render("Owner: {{ticket.owner}} / {{requester.name}}");

        Assert.Equal("Owner: {{ticket.owner}} / Ada Lovelace", body);
    }

    [Fact]
    public void Render_TextWithoutPlaceholders_IsUnchanged()
    {
        var body = Render("A plain reply with { braces } and no tokens.");

        Assert.Equal("A plain reply with { braces } and no tokens.", body);
    }

    [Fact]
    public void SupportedPlaceholders_AllResolve()
    {
        foreach (var placeholder in CannedResponseRenderer.SupportedPlaceholders)
        {
            Assert.NotEqual($"{{{{{placeholder}}}}}", Render($"{{{{{placeholder}}}}}"));
        }
    }

    private static string Render(string body) => CannedResponseRenderer.Render(body, Values);
}
