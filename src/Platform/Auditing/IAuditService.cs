using System.Security.Claims;

namespace Platform.Auditing;

public interface IAuditService
{
    Task WriteAsync(
        ClaimsPrincipal actor,
        string action,
        string entityType,
        string entityId,
        object? before,
        object? after,
        CancellationToken cancellationToken = default);
}