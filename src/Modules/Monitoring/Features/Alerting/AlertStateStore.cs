using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using StackExchange.Redis;

namespace Modules.Monitoring.Features.Alerting;

public interface IAlertStateStore
{
    /// <summary>
    /// The rule's remembered state, or a fresh one. Never throws on a Redis failure: an alert engine
    /// that stops evaluating because a cache is unavailable turns one outage into two.
    /// </summary>
    Task<AlertState> ReadAsync(Guid deviceId, string ruleId, CancellationToken cancellationToken);

    Task WriteAsync(Guid deviceId, string ruleId, AlertState state, CancellationToken cancellationToken);
}

/// <summary>
/// Alert state in Redis, which is where ARCHITECTURE §5 puts it. Redis is deliberately not the source
/// of truth: the durable half is the <c>monitoring.alerts</c> row, and <see cref="AlertEngine"/>
/// rebuilds a missing state from it, so flushing Redis costs the N-cycle counters and the flap
/// history rather than re-raising every open alert.
/// </summary>
public sealed class RedisAlertStateStore(
    IConnectionMultiplexer redis,
    IOptions<AlertOptions> options,
    ILogger<RedisAlertStateStore> logger) : IAlertStateStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public async Task<AlertState> ReadAsync(Guid deviceId, string ruleId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var value = await redis.GetDatabase().StringGetAsync(Key(deviceId, ruleId));
            if (value.IsNullOrEmpty)
            {
                return new AlertState();
            }

            // Explicitly a string: RedisValue converts implicitly to both string and byte span, which
            // makes the Deserialize overload ambiguous.
            return JsonSerializer.Deserialize<AlertState>((string)value!, SerializerOptions)
                ?? new AlertState();
        }
        catch (Exception error) when (error is RedisException or JsonException)
        {
            // A rule that has forgotten its counters starts them again; the durable alert row keeps
            // it from also forgetting that it is already alerting.
            logger.LogWarning(error, "Alert state for {RuleId} on device {DeviceId} could not be read.",
                ruleId, deviceId);
            return new AlertState();
        }
    }

    public async Task WriteAsync(
        Guid deviceId,
        string ruleId,
        AlertState state,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await redis.GetDatabase().StringSetAsync(
                Key(deviceId, ruleId),
                JsonSerializer.Serialize(state, SerializerOptions),
                // Refreshed on every write, so the key lives as long as the rule is being polled and
                // no longer. A check deleted a year ago is not still holding one.
                TimeSpan.FromDays(options.Value.StateTtlDays));
        }
        catch (RedisException error)
        {
            logger.LogWarning(error, "Alert state for {RuleId} on device {DeviceId} could not be written.",
                ruleId, deviceId);
        }
    }

    private static string Key(Guid deviceId, string ruleId) => $"monitoring:alert-state:{deviceId}:{ruleId}";
}
