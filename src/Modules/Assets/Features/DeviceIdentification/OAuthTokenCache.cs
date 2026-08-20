namespace Modules.Assets.Features.DeviceIdentification;

/// <summary>
/// Access tokens, one per manufacturer, held for the process rather than for a request.
/// <para>
/// A singleton because the provider that uses it is scoped: a token is good for about an hour, and
/// fetching one per identification would triple the traffic to Dell and spend rate limit on nothing.
/// A service rather than a static field so a test can hand over a fresh one instead of reaching into
/// the provider to reset it — shared mutable statics are how one test case starts depending on
/// whichever ran before it.
/// </para>
/// </summary>
public sealed class OAuthTokenCache
{
    /// <summary>One entry per provider: Dell's token is no use to Cisco.</summary>
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _lock = new(1, 1);

    private sealed class Entry
    {
        public string? Token { get; set; }
        public DateTimeOffset ExpiresAt { get; set; } = DateTimeOffset.MinValue;
    }

    /// <summary>
    /// The cached token, or one fetched by <paramref name="fetch"/>. The fetch runs under a lock and
    /// the cache is re-checked inside it: several identifications can arrive together, and without
    /// that they would each fetch a token the others had already made unnecessary.
    /// </summary>
    public async Task<string?> GetAsync(
        string provider,
        TimeSpan renewalMargin,
        Func<CancellationToken, Task<(string Token, TimeSpan Lifetime)?>> fetch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fetch);
        if (Fresh(provider, renewalMargin) is { } cached) return cached;

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (Fresh(provider, renewalMargin) is { } raced) return raced;

            var fetched = await fetch(cancellationToken);
            if (fetched is null) return null;

            _entries[provider] = new Entry
            {
                Token = fetched.Value.Token,
                ExpiresAt = DateTimeOffset.UtcNow + fetched.Value.Lifetime,
            };
            return fetched.Value.Token;
        }
        finally
        {
            _lock.Release();
        }
    }

    private string? Fresh(string provider, TimeSpan margin) =>
        _entries.TryGetValue(provider, out var entry)
        && entry.Token is not null
        && DateTimeOffset.UtcNow < entry.ExpiresAt - margin
            ? entry.Token
            : null;
}
