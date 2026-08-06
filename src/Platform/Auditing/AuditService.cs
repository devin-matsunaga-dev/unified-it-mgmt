using System.Security.Claims;
using System.Text.Json;

using Microsoft.AspNetCore.Http;

using Platform.Data;

namespace Platform.Auditing;

public sealed class AuditService(PlatformDbContext dbContext, IHttpContextAccessor httpContextAccessor) : IAuditService
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public async Task WriteAsync(
        ClaimsPrincipal actor,
        string action,
        string entityType,
        string entityId,
        object? before,
        object? after,
        CancellationToken cancellationToken = default)
    {
        var actorId = actor.FindFirstValue("sub") ?? actor.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(actorId))
        {
            throw new InvalidOperationException("An authenticated actor identifier is required for auditing.");
        }

        var httpContext = httpContextAccessor.HttpContext;
        var correlationId = httpContext?.Items[CorrelationIdMiddleware.ItemKey]?.ToString()
            ?? httpContext?.TraceIdentifier
            ?? Guid.CreateVersion7().ToString();

        dbContext.AuditEntries.Add(new AuditEntry
        {
            Id = Guid.CreateVersion7(),
            ActorId = actorId,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            BeforeJson = Serialize(before),
            AfterJson = Serialize(after),
            OccurredAt = DateTimeOffset.UtcNow,
            CorrelationId = correlationId,
        });
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static string? Serialize(object? value) =>
        value is null ? null : JsonSerializer.Serialize(value, SerializerOptions);
}