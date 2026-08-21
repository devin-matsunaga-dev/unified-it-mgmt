using Modules.Assets.Data;
using Modules.Assets.Features.Contracts;

namespace Infrastructure.Tests;

/// <summary>
/// Who a renewal notice reaches and what it says. The recipient list and the detail line are the two
/// halves of "tell the contracts team what is expiring", so they are covered together.
/// </summary>
public sealed class ContractReminderRecipientTests
{
    private static readonly DateOnly Today = new(2026, 8, 21);

    private static ContractExpiryNotice Notice(string recipient, string? details, int dueInDays = 30)
    {
        var candidate = new ContractExpiryCandidate(
            ContractNotificationSubject.Contract,
            Guid.NewGuid(),
            "Support contract PO - 4821 (ProSupport, Dell)",
            Today.AddDays(dueInDays),
            recipient,
            details);
        return Assert.Single(ContractExpiryPlanner.Plan(
            [candidate], Today, new HashSet<ContractNotificationKey>(), [30]));
    }

    /// <summary>
    /// The reason the detail line exists: somebody reading this on a phone should know who the contract
    /// is with and what it costs without opening the platform.
    /// </summary>
    [Fact]
    public void Message_WithDetails_PutsThemUnderTheDeadline()
    {
        var message = Notice("contracts@example.test", "Vendor: Dell · Department: Finance · Cost: 12,400.00 USD").Message;

        var lines = message.Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.Contains("expires in 30 days on 2026-09-20", lines[0]);
        Assert.Equal("Vendor: Dell · Department: Finance · Cost: 12,400.00 USD", lines[1]);
    }

    /// <summary>
    /// A warranty notice carries no details, and must not gain a trailing blank line for it — the body
    /// is the same one-line sentence it was before contracts grew a detail line.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Message_WithoutDetails_IsTheHeadlineAlone(string? details)
    {
        var message = Notice("owner@example.test", details).Message;

        Assert.DoesNotContain('\n', message);
        Assert.EndsWith("expires in 30 days on 2026-09-20.", message);
    }

    /// <summary>
    /// Several addresses travel as one recorded recipient and are split at the point of sending, so the
    /// row says who was written to while each mailbox still gets its own message.
    /// </summary>
    [Fact]
    public void Recipient_WithSeveralAddresses_SplitsBackIntoOnePerMailbox()
    {
        var recipient = string.Join(
            ContractExpiryService.RecipientSeparator, ["contracts@example.test", "finance@example.test"]);

        var addresses = Notice(recipient, null).Candidate.Recipient.Split(
            ContractExpiryService.RecipientSeparator,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        Assert.Equal(["contracts@example.test", "finance@example.test"], addresses);
    }
}
