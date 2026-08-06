using Microsoft.EntityFrameworkCore;

using Platform.Data;

namespace Platform.Messaging;

public sealed class ConsumerIdempotencyService(PlatformDbContext dbContext) : IConsumerIdempotencyService
{
    public async Task<bool> ExecuteOnceAsync(
        string dedupeKey,
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dedupeKey);
        ArgumentNullException.ThrowIfNull(action);

        if (await dbContext.ConsumerDedupeEntries.AnyAsync(
                entry => entry.Key == dedupeKey,
                cancellationToken))
        {
            return false;
        }

        var ownsTransaction = dbContext.Database.CurrentTransaction is null;
        await using var transaction = ownsTransaction
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;
        dbContext.ConsumerDedupeEntries.Add(new ConsumerDedupeEntry
        {
            Key = dedupeKey,
            ConsumedAt = DateTimeOffset.UtcNow,
        });
        await action(cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return true;
    }
}
