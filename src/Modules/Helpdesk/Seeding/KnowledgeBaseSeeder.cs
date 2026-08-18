using Microsoft.EntityFrameworkCore;

using Modules.Helpdesk.Data;

namespace Modules.Helpdesk.Seeding;

public sealed record KnowledgeBaseSeedResult(int ArticlesAdded, int RevisionsAdded);

/// <summary>
/// A knowledge base with something in it, so WP-5.9's three verification steps are walkable a minute after
/// <c>aspire run</c> with nothing to write.
/// <para>
/// Four articles and not forty. Each one is here to make a specific claim demonstrable: a published article
/// the VPN incident everybody types will surface, a second published one that answers the seeded Wi-Fi
/// recurrence, one carrying a nickname nothing in its prose says (so the keyword weighting can be seen to
/// work), and <b>one draft</b> — which is the only way "portal search finds published only" can be shown to
/// be true rather than merely claimed.
/// </para>
/// <para>
/// The draft also carries a revision, so the version history opens on something rather than on an empty
/// panel that reads as a feature that did not ship.
/// </para>
/// </summary>
public sealed class KnowledgeBaseSeeder(HelpdeskDbContext dbContext)
{
    private const int ArticleKind = 59;
    private const int RevisionKind = 60;
    private const string ActorId = "seeder";
    private const string ActorName = "Demo seeder";

    /// <summary>Seeded categories from <see cref="HelpdeskDemoDataSeeder"/>.</summary>
    private static readonly Guid NetworkCategoryId = Guid.Parse("01980000-0000-7000-8000-000000000504");

    private sealed record SeedArticle(
        string Title,
        string Summary,
        string Body,
        string? Keywords,
        KbArticleStatus Status,
        Guid? CategoryId);

    private static readonly SeedArticle[] Articles =
    [
        new("Connecting to the VPN from home",
            "How to sign in to the corporate VPN, and what to try first when it will not connect.",
            """
            ## Before you start

            You need your usual sign-in, your phone for the second factor, and a working internet connection.

            ## Connecting

            1. Open the VPN client from the Start menu.
            2. Leave the server as **vpn.corp** and choose *Connect*.
            3. Approve the prompt on your phone.

            ## If it will not connect

            - Check that an ordinary web page loads first. A VPN cannot fix an internet connection that is down.
            - Sign out of the client and back in; a stale token is the commonest cause.
            - If the second-factor prompt never arrives, your phone has probably lost notifications — open the
              authenticator app directly and read the code from there.
            - Hotel and café networks often block the VPN outright. Tethering from a phone is the quickest test.
            """,
            "vpn, remote access, work from home, anyconnect",
            KbArticleStatus.Published,
            null),

        new("Wi-Fi drops for a minute at a time",
            "Short, repeated drop-outs on the office Wi-Fi — what causes them and what to do while it is being fixed.",
            """
            ## What you will see

            The network disappears for around a minute and then comes back on its own, several times an hour.
            Calls freeze, file shares vanish, and everything looks fine again by the time anybody looks.

            ## What causes it

            An access point that is flapping — losing its uplink and re-joining. It affects everybody in range
            of that access point and nobody outside it, which is why the person at the next desk may be fine.

            ## What to do now

            - Move to a desk near a different access point if you can; the drop-outs stop immediately.
            - Use a wired connection for anything that must not be interrupted, such as a customer call.
            - Raise a ticket saying **which floor and which side of the building** you are on. That is the one
              detail that turns "the Wi-Fi is bad" into something somebody can fix.
            """,
            "wifi, wireless, dropping, disconnects, flapping",
            KbArticleStatus.Published,
            NetworkCategoryId),

        new("Resetting your password",
            "Change or reset the password you sign in with, including when you are locked out.",
            """
            ## Changing a password you still know

            Press **Ctrl+Alt+Delete** and choose *Change a password*.

            ## Resetting one you have forgotten

            Use the self-service reset page and answer the second-factor prompt. If you are already locked out,
            the account unlocks itself thirty minutes after the last failed attempt — a reset does not shorten
            that wait, so raise a ticket if you cannot wait it out.

            ## Passwords expire every 180 days

            You are warned for fourteen days beforehand. The warning only appears when you sign in at a desk,
            so somebody working remotely for a fortnight can be expired without ever having seen it.
            """,
            "password, locked out, forgot password, unlock, pwreset",
            KbArticleStatus.Published,
            null),

        // Deliberately a draft. It is the row that makes "the portal finds published articles only" a thing
        // somebody can check rather than a claim they have to believe.
        new("Requesting a new laptop",
            "Draft — the procurement steps are still being agreed with Finance.",
            """
            ## Who can request one

            Anyone whose current machine is more than four years old, or whose role has changed.

            ## What happens next

            *This section is unfinished: the approval path is still being agreed with Finance and this article
            must not be published until it is.*
            """,
            "laptop, hardware request, procurement",
            KbArticleStatus.Draft,
            null),
    ];

    public async Task<KnowledgeBaseSeedResult> SeedAsync(CancellationToken cancellationToken = default)
    {
        var categoryExists = await dbContext.TicketCategories
            .AnyAsync(category => category.Id == NetworkCategoryId, cancellationToken);

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var articlesAdded = 0;
        var revisionsAdded = 0;

        for (var index = 0; index < Articles.Length; index++)
        {
            var articleId = DeterministicId(ArticleKind, index);
            if (await dbContext.KbArticles.AnyAsync(article => article.Id == articleId, cancellationToken))
            {
                continue;
            }

            var seed = Articles[index];
            var published = seed.Status == KbArticleStatus.Published;
            // Staggered so the browse list has an order that is not the order they were written in.
            var createdAt = now - TimeSpan.FromDays(30 - (index * 4));
            var updatedAt = createdAt + TimeSpan.FromDays(1);

            dbContext.KbArticles.Add(new KbArticle
            {
                Id = articleId,
                Title = seed.Title,
                Summary = seed.Summary,
                Body = seed.Body,
                Keywords = seed.Keywords,
                Status = seed.Status,
                CategoryId = seed.CategoryId is not null && categoryExists ? seed.CategoryId : null,
                // The second article is on its second version, which is what gives the history panel
                // something to show and the restore button something to do.
                Version = index == 1 ? 2 : 1,
                AuthorId = ActorId,
                AuthorName = ActorName,
                UpdatedById = ActorId,
                UpdatedByName = ActorName,
                PublishedById = published ? ActorId : null,
                PublishedByName = published ? ActorName : null,
                PublishedAt = published ? updatedAt : null,
                CreatedAt = createdAt,
                UpdatedAt = updatedAt,
            });
            articlesAdded++;

            if (index == 1)
            {
                dbContext.KbArticleRevisions.Add(new KbArticleRevision
                {
                    Id = DeterministicId(RevisionKind, index),
                    ArticleId = articleId,
                    Version = 1,
                    Title = "Wi-Fi problems",
                    Summary = "The Wi-Fi keeps dropping.",
                    Body =
                        "The Wi-Fi drops sometimes. We are looking into it.\n\n"
                        + "*(The first version of this article, kept so the history has something in it.)*",
                    Keywords = "wifi",
                    CategoryId = seed.CategoryId is not null && categoryExists ? seed.CategoryId : null,
                    AuthorId = ActorId,
                    AuthorName = ActorName,
                    CreatedAt = updatedAt,
                });
                revisionsAdded++;
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new KnowledgeBaseSeedResult(articlesAdded, revisionsAdded);
    }

    private static Guid DeterministicId(int kind, int index) =>
        Guid.Parse($"01980001-{kind:0000}-7000-8000-{index:0000}00000000");
}
