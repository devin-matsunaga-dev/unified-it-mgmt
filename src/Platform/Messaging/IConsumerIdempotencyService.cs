namespace Platform.Messaging;

public interface IConsumerIdempotencyService
{
    Task<bool> ExecuteOnceAsync(
        string dedupeKey,
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken = default);
}
