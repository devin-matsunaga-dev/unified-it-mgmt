using Modules.Helpdesk.Data;

namespace Modules.Helpdesk.Features.Knowledge;

/// <summary>Why a requested state change was refused, or <see cref="Allowed"/>.</summary>
public enum KbTransitionVerdict
{
    Allowed,

    /// <summary>The article is already in that state.</summary>
    NoChange,

    /// <summary>Not a move this workflow makes.</summary>
    NotPermitted,

    /// <summary>
    /// Publishing needs a summary and a body. The entry condition that makes the knowledge base worth
    /// searching: a published article with nothing in it stops somebody looking further.
    /// </summary>
    NeedsContent,
}

/// <summary>
/// The article lifecycle, as a table rather than as a chain of <c>if</c>s in the service — the shape
/// <see cref="Problems.ProblemWorkflow"/> and <c>ChangeWorkflow</c> both use, and for the same reason: a
/// state machine somebody can read is one they can argue with.
/// <para>
/// Like the problem's and unlike the ticket's, entry into <see cref="KbArticleStatus.Published"/> carries a
/// <em>condition</em> and not just a source.
/// </para>
/// </summary>
public static class KbWorkflow
{
    /// <summary>
    /// Where each state can go. Publishing is reversible in both directions — an article pulled back to
    /// draft is being corrected, an archived one revived is being brought back into use — because the
    /// alternative is a second article about the same thing, which is how a knowledge base starts
    /// contradicting itself.
    /// </summary>
    private static readonly Dictionary<KbArticleStatus, KbArticleStatus[]> Allowed = new()
    {
        [KbArticleStatus.Draft] = [KbArticleStatus.Published, KbArticleStatus.Archived],
        [KbArticleStatus.Published] = [KbArticleStatus.Draft, KbArticleStatus.Archived],
        [KbArticleStatus.Archived] = [KbArticleStatus.Draft, KbArticleStatus.Published],
    };

    public static IReadOnlyList<KbArticleStatus> NextFrom(KbArticleStatus status) =>
        Allowed.TryGetValue(status, out var targets) ? targets : [];

    public static KbTransitionVerdict Check(KbArticle article, KbArticleStatus target)
    {
        ArgumentNullException.ThrowIfNull(article);

        if (article.Status == target)
        {
            return KbTransitionVerdict.NoChange;
        }

        if (!NextFrom(article.Status).Contains(target))
        {
            return KbTransitionVerdict.NotPermitted;
        }

        if (target == KbArticleStatus.Published
            && (string.IsNullOrWhiteSpace(article.Summary) || string.IsNullOrWhiteSpace(article.Body)))
        {
            return KbTransitionVerdict.NeedsContent;
        }

        return KbTransitionVerdict.Allowed;
    }

    /// <summary>The message a refusal carries, which is the only thing the person who tried it will read.</summary>
    public static string Explain(KbArticleStatus from, KbArticleStatus target, KbTransitionVerdict verdict) => verdict switch
    {
        KbTransitionVerdict.NoChange => $"This article is already {from}.",
        KbTransitionVerdict.NotPermitted =>
            $"An article cannot go from {from} to {target}. From {from} it can become "
            + $"{string.Join(", ", NextFrom(from))}.",
        KbTransitionVerdict.NeedsContent =>
            "A published article needs a summary and a body — somebody who finds it stops looking, so it "
            + "has to answer them.",
        _ => string.Empty,
    };
}
