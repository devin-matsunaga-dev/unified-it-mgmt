using Modules.Helpdesk.Data;
using Modules.Helpdesk.Features.Knowledge;

namespace Infrastructure.Tests;

/// <summary>
/// The article lifecycle on its own, with no database (WP-5.9). The interesting half is the entry
/// condition on <see cref="KbArticleStatus.Published"/>: everything else is a table, and a table nobody
/// tests is a table somebody edits.
/// </summary>
public sealed class KbWorkflowTests
{
    [Theory]
    [InlineData(KbArticleStatus.Draft, KbArticleStatus.Published)]
    [InlineData(KbArticleStatus.Draft, KbArticleStatus.Archived)]
    [InlineData(KbArticleStatus.Published, KbArticleStatus.Draft)]
    [InlineData(KbArticleStatus.Published, KbArticleStatus.Archived)]
    [InlineData(KbArticleStatus.Archived, KbArticleStatus.Draft)]
    [InlineData(KbArticleStatus.Archived, KbArticleStatus.Published)]
    public void Check_AMoveTheWorkflowMakes_IsAllowed(KbArticleStatus from, KbArticleStatus target) =>
        Assert.Equal(KbTransitionVerdict.Allowed, KbWorkflow.Check(Article(from), target));

    /// <summary>
    /// The entry condition, which is what makes the knowledge base worth searching: somebody who finds an
    /// article stops looking, so a published one has to answer them.
    /// </summary>
    [Theory]
    [InlineData("", "A body.")]
    [InlineData("A summary.", "")]
    [InlineData("   ", "   ")]
    public void Check_PublishingWithoutContent_NeedsContent(string summary, string body)
    {
        var article = Article(KbArticleStatus.Draft);
        article.Summary = summary;
        article.Body = body;

        Assert.Equal(
            KbTransitionVerdict.NeedsContent,
            KbWorkflow.Check(article, KbArticleStatus.Published));
    }

    /// <summary>
    /// The condition is on <em>publishing</em> and not on the article, so an empty draft can still be
    /// archived. Somebody abandoning a draft must not be made to finish it first.
    /// </summary>
    [Fact]
    public void Check_ArchivingAnEmptyDraft_IsAllowed()
    {
        var article = Article(KbArticleStatus.Draft);
        article.Summary = string.Empty;
        article.Body = string.Empty;

        Assert.Equal(KbTransitionVerdict.Allowed, KbWorkflow.Check(article, KbArticleStatus.Archived));
    }

    [Fact]
    public void Check_TheStateItIsAlreadyIn_IsNoChange() =>
        Assert.Equal(
            KbTransitionVerdict.NoChange,
            KbWorkflow.Check(Article(KbArticleStatus.Published), KbArticleStatus.Published));

    /// <summary>
    /// Every state is reachable from every other, deliberately: an article pulled back to draft is being
    /// corrected and an archived one revived is being brought back into use, and refusing either is how a
    /// knowledge base ends up with two articles about the same thing.
    /// </summary>
    [Theory]
    [InlineData(KbArticleStatus.Draft)]
    [InlineData(KbArticleStatus.Published)]
    [InlineData(KbArticleStatus.Archived)]
    public void NextFrom_EveryState_OffersTheOtherTwo(KbArticleStatus from)
    {
        var next = KbWorkflow.NextFrom(from);

        Assert.Equal(2, next.Count);
        Assert.DoesNotContain(from, next);
    }

    /// <summary>The refusal has to say what to do about it — it is the only thing the person who tried it reads.</summary>
    [Fact]
    public void Explain_NeedsContent_SaysWhatIsMissing()
    {
        var message = KbWorkflow.Explain(
            KbArticleStatus.Draft, KbArticleStatus.Published, KbTransitionVerdict.NeedsContent);

        Assert.Contains("summary", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("body", message, StringComparison.OrdinalIgnoreCase);
    }

    private static KbArticle Article(KbArticleStatus status) => new()
    {
        Id = Guid.CreateVersion7(),
        Title = "How to do the thing",
        Summary = "The short version.",
        Body = "The long version.",
        Status = status,
    };
}
