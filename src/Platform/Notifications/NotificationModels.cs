using Platform.Data;

namespace Platform.Notifications;

public enum NotificationOutcome
{
    Success,
    NotFound,
    Invalid,
    Conflict,
}

public sealed class NotificationOptions
{
    public const string SectionName = "Notifications";

    /// <summary>
    /// Where a deep link points. Falls back to <c>WebClient:Origin</c>, following the WP-2.7 label
    /// base-URL precedent — a link in a chat message outlives the process that wrote it, so
    /// "localhost" is only ever right on the host's own machine.
    /// </summary>
    public string? DeepLinkBaseUrl { get; set; }

    /// <summary>How often the digest pass looks for quiet-hours notifications that are now due.</summary>
    public int DigestIntervalSeconds { get; set; } = 300;
}

/// <param name="TargetRedacted">
/// Never the real destination. A webhook URL is a bearer credential — anyone holding it can post into
/// the channel — so it follows ARCHITECTURE §7's vault rule: write-only, and reads answer with enough
/// to tell two channels apart and nothing more.
/// </param>
public sealed record NotificationChannelResponse(
    Guid Id,
    string Name,
    NotificationChannelKind Kind,
    string TargetRedacted,
    string? Description,
    bool IsActive,
    int RuleCount,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    string UpdatedBy,
    DateTimeOffset UpdatedAt);

public sealed record CreateNotificationChannelRequest(
    string Name,
    NotificationChannelKind Kind,
    string Target,
    string? Description,
    bool IsActive);

/// <param name="Target">
/// Null leaves the stored destination alone, which is what makes an edit of the name or the active
/// flag possible without the client having to know a value no read has ever returned it.
/// </param>
public sealed record UpdateNotificationChannelRequest(
    string Name,
    string? Target,
    string? Description,
    bool IsActive);

public sealed record NotificationRoutingRuleResponse(
    Guid Id,
    string Name,
    Guid ChannelId,
    string ChannelName,
    NotificationChannelKind ChannelKind,
    string? EventKind,
    NotificationSeverity MinimumSeverity,
    string? DeviceGroup,
    TimeOnly? QuietHoursStart,
    TimeOnly? QuietHoursEnd,
    string TimeZone,
    bool DigestQuietHours,
    bool IsActive,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    string UpdatedBy,
    DateTimeOffset UpdatedAt);

public sealed record SaveNotificationRoutingRuleRequest(
    string Name,
    Guid ChannelId,
    string? EventKind,
    NotificationSeverity MinimumSeverity,
    string? DeviceGroup,
    TimeOnly? QuietHoursStart,
    TimeOnly? QuietHoursEnd,
    string? TimeZone,
    bool DigestQuietHours,
    bool IsActive);

/// <param name="IsConfigured">
/// False when nothing has been saved and the answer is the permissive default. A screen that cannot
/// tell the two apart shows a preference the user never expressed as one they did.
/// </param>
public sealed record UserNotificationPreferenceResponse(
    string UserId,
    string? EmailAddress,
    bool EmailEnabled,
    NotificationSeverity MinimumSeverity,
    TimeOnly? QuietHoursStart,
    TimeOnly? QuietHoursEnd,
    string TimeZone,
    bool DigestQuietHours,
    bool IsConfigured,
    DateTimeOffset? UpdatedAt);

public sealed record SaveUserNotificationPreferenceRequest(
    string? EmailAddress,
    bool EmailEnabled,
    NotificationSeverity MinimumSeverity,
    TimeOnly? QuietHoursStart,
    TimeOnly? QuietHoursEnd,
    string? TimeZone,
    bool DigestQuietHours);

public sealed record NotificationDeliveryResponse(
    Guid Id,
    string EventKind,
    NotificationSeverity Severity,
    string Subject,
    string? DeepLink,
    string? DedupeKey,
    Guid? ChannelId,
    string? ChannelName,
    NotificationChannelKind ChannelKind,
    string TargetRedacted,
    string? UserId,
    Guid? RuleId,
    NotificationDeliveryOutcome Outcome,
    string? Detail,
    DateTimeOffset? ReleaseAfter,
    Guid? DigestDeliveryId,
    int? DigestOfCount,
    DateTimeOffset OccurredAt,
    DateTimeOffset? CompletedAt);

public sealed record NotificationDeliveryListRequest(
    string? EventKind,
    NotificationDeliveryOutcome? Outcome,
    Guid? ChannelId,
    string? UserId,
    int Page,
    int PageSize);

public sealed record NotificationDeliveryPageResponse(
    IReadOnlyList<NotificationDeliveryResponse> Items,
    int Total,
    int Page,
    int PageSize);

public sealed record NotificationChannelResult(
    NotificationOutcome Outcome,
    NotificationChannelResponse? Channel = null,
    IReadOnlyDictionary<string, string[]>? Errors = null,
    string? Error = null);

public sealed record NotificationRoutingRuleResult(
    NotificationOutcome Outcome,
    NotificationRoutingRuleResponse? Rule = null,
    IReadOnlyDictionary<string, string[]>? Errors = null,
    string? Error = null);

public sealed record UserNotificationPreferenceResult(
    NotificationOutcome Outcome,
    UserNotificationPreferenceResponse? Preference = null,
    IReadOnlyDictionary<string, string[]>? Errors = null);
