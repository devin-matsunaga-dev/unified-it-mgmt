namespace Modules.Assets.Features.DeviceIdentification.Dell;

/// <summary>
/// One access token, held for the process rather than for a request.
/// <para>
/// A singleton because the provider that uses it is scoped: a token is good for about an hour, and
/// fetching one per identification would triple the traffic to Dell and spend rate limit on nothing.
/// A service rather than a static field so a test can hand over a fresh one instead of reaching into
/// the provider to reset it — shared mutable statics are how one test case starts depending on
/// whichever ran before it.
/// </para>
/// </summary>
public sealed class DellTokenCache
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private string? _token;
    private DateTimeOffset _expiresAt = DateTimeOffset.MinValue;

    /// <summary>
    /// The cached token, or one fetched by <paramref name="fetch"/>. The fetch runs under a lock and
    /// the cache is re-checked inside it: several identifications can arrive together, and without
    /// that they would each fetch a token the others had already made unnecessary.
    /// </summary>
    public async Task<string?> GetAsync(
        TimeSpan renewalMargin,
        Func<CancellationToken, Task<(string Token, TimeSpan Lifetime)?>> fetch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fetch);
        if (IsFresh(renewalMargin)) return _token;

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (IsFresh(renewalMargin)) return _token;

            var fetched = await fetch(cancellationToken);
            if (fetched is null) return null;

            _token = fetched.Value.Token;
            _expiresAt = DateTimeOffset.UtcNow + fetched.Value.Lifetime;
            return _token;
        }
        finally
        {
            _lock.Release();
        }
    }

    private bool IsFresh(TimeSpan margin) =>
        _token is not null && DateTimeOffset.UtcNow < _expiresAt - margin;
}
