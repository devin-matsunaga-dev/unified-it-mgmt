using System.Net.Mail;
using System.Security.Claims;

using Microsoft.EntityFrameworkCore;

using Platform.Auditing;
using Platform.Data;

namespace Platform.Notifications;

public interface INotificationRoutingService
{
    Task<IReadOnlyList<NotificationChannelResponse>> ListChannelsAsync(CancellationToken cancellationToken);

    Task<NotificationChannelResult> CreateChannelAsync(
        CreateNotificationChannelRequest request, ClaimsPrincipal actor, CancellationToken cancellationToken);

    Task<NotificationChannelResult> UpdateChannelAsync(
        Guid id, UpdateNotificationChannelRequest request, ClaimsPrincipal actor, CancellationToken cancellationToken);

    Task<NotificationChannelResult> DeleteChannelAsync(
        Guid id, ClaimsPrincipal actor, CancellationToken cancellationToken);

    Task<IReadOnlyList<NotificationRoutingRuleResponse>> ListRulesAsync(CancellationToken cancellationToken);

    Task<NotificationRoutingRuleResult> CreateRuleAsync(
        SaveNotificationRoutingRuleRequest request, ClaimsPrincipal actor, CancellationToken cancellationToken);

    Task<NotificationRoutingRuleResult> UpdateRuleAsync(
        Guid id, SaveNotificationRoutingRuleRequest request, ClaimsPrincipal actor, CancellationToken cancellationToken);

    Task<NotificationOutcome> DeleteRuleAsync(Guid id, ClaimsPrincipal actor, CancellationToken cancellationToken);

    Task<UserNotificationPreferenceResponse> GetPreferenceAsync(string userId, CancellationToken cancellationToken);

    Task<UserNotificationPreferenceResult> SavePreferenceAsync(
        SaveUserNotificationPreferenceRequest request, ClaimsPrincipal actor, CancellationToken cancellationToken);

    Task<NotificationDeliveryPageResponse> ListDeliveriesAsync(
        NotificationDeliveryListRequest request, CancellationToken cancellationToken);
}

public sealed class NotificationRoutingService(PlatformDbContext dbContext, IAuditService auditService)
    : INotificationRoutingService
{
    private const int MaximumPageSize = 200;

    public async Task<IReadOnlyList<NotificationChannelResponse>> ListChannelsAsync(CancellationToken cancellationToken)
    {
        var channels = await dbContext.NotificationChannels.AsNoTracking()
            .Include(channel => channel.Rules)
            .OrderBy(channel => channel.Name)
            .ToListAsync(cancellationToken);
        return [.. channels.Select(Map)];
    }

    public async Task<NotificationChannelResult> CreateChannelAsync(
        CreateNotificationChannelRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (ValidateTarget(request.Kind, request.Target) is { Count: > 0 } errors)
        {
            return new(NotificationOutcome.Invalid, Errors: errors);
        }

        var name = request.Name.Trim();
        if (await dbContext.NotificationChannels.AnyAsync(channel => channel.Name == name, cancellationToken))
        {
            return new(NotificationOutcome.Conflict, Error: $"A notification channel named '{name}' already exists.");
        }

        var actorId = ActorId(actor);
        var now = DateTimeOffset.UtcNow;
        var created = new NotificationChannel
        {
            Id = Guid.CreateVersion7(),
            Name = name,
            Kind = request.Kind,
            Target = request.Target.Trim(),
            Description = Trim(request.Description),
            IsActive = request.IsActive,
            CreatedBy = actorId,
            CreatedAt = now,
            UpdatedBy = actorId,
            UpdatedAt = now,
        };
        dbContext.NotificationChannels.Add(created);
        await dbContext.SaveChangesAsync(cancellationToken);

        var response = Map(created);
        // The audit entry carries the redacted response, never the entity: an audit log that records
        // the webhook URL hands it to everyone who can read the log.
        await auditService.WriteAsync(
            actor, "Created", "NotificationChannel", created.Id.ToString(), null, response, cancellationToken);
        return new(NotificationOutcome.Success, response);
    }

    public async Task<NotificationChannelResult> UpdateChannelAsync(
        Guid id,
        UpdateNotificationChannelRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var channel = await dbContext.NotificationChannels
            .Include(item => item.Rules)
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (channel is null)
        {
            return new(NotificationOutcome.NotFound);
        }

        // The kind is immutable: it decides the wire format, and every rule pointing here was written
        // against it. Changing a Teams channel into an email address is a new channel.
        if (request.Target is { Length: > 0 } target && ValidateTarget(channel.Kind, target) is { Count: > 0 } errors)
        {
            return new(NotificationOutcome.Invalid, Errors: errors);
        }

        var name = request.Name.Trim();
        if (await dbContext.NotificationChannels
                .AnyAsync(item => item.Name == name && item.Id != id, cancellationToken))
        {
            return new(NotificationOutcome.Conflict, Error: $"A notification channel named '{name}' already exists.");
        }

        var before = Map(channel);
        channel.Name = name;
        channel.Description = Trim(request.Description);
        channel.IsActive = request.IsActive;
        if (request.Target is { Length: > 0 } rotated)
        {
            channel.Target = rotated.Trim();
        }

        channel.UpdatedBy = ActorId(actor);
        channel.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);

        var response = Map(channel);
        await auditService.WriteAsync(
            actor, "Updated", "NotificationChannel", id.ToString(), before, response, cancellationToken);
        return new(NotificationOutcome.Success, response);
    }

    public async Task<NotificationChannelResult> DeleteChannelAsync(
        Guid id,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        var channel = await dbContext.NotificationChannels
            .Include(item => item.Rules)
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (channel is null)
        {
            return new(NotificationOutcome.NotFound);
        }

        // Mirrors the WP-2.6 contract/vendor guard. Deleting the routing along with the channel is how
        // a Critical alert quietly stops reaching anybody.
        if (channel.Rules.Count > 0)
        {
            return new(NotificationOutcome.Conflict,
                Error: $"{channel.Rules.Count} routing rule(s) still send to this channel. Remove them first.");
        }

        var before = Map(channel);
        dbContext.NotificationChannels.Remove(channel);
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditService.WriteAsync(
            actor, "Deleted", "NotificationChannel", id.ToString(), before, null, cancellationToken);
        return new(NotificationOutcome.Success);
    }

    public async Task<IReadOnlyList<NotificationRoutingRuleResponse>> ListRulesAsync(CancellationToken cancellationToken)
    {
        var rules = await dbContext.NotificationRoutingRules.AsNoTracking()
            .Include(rule => rule.Channel)
            .OrderBy(rule => rule.Name)
            .ToListAsync(cancellationToken);
        return [.. rules.Select(Map)];
    }

    public async Task<NotificationRoutingRuleResult> CreateRuleAsync(
        SaveNotificationRoutingRuleRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var channel = await dbContext.NotificationChannels
            .SingleOrDefaultAsync(item => item.Id == request.ChannelId, cancellationToken);
        if (ValidateRule(request, channel) is { Count: > 0 } errors)
        {
            return new(NotificationOutcome.Invalid, Errors: errors);
        }

        var name = request.Name.Trim();
        if (await dbContext.NotificationRoutingRules.AnyAsync(rule => rule.Name == name, cancellationToken))
        {
            return new(NotificationOutcome.Conflict, Error: $"A routing rule named '{name}' already exists.");
        }

        var actorId = ActorId(actor);
        var now = DateTimeOffset.UtcNow;
        var rule = new NotificationRoutingRule
        {
            Id = Guid.CreateVersion7(),
            Name = name,
            ChannelId = request.ChannelId,
            Channel = channel!,
            EventKind = Trim(request.EventKind),
            MinimumSeverity = request.MinimumSeverity,
            DeviceGroup = Trim(request.DeviceGroup),
            QuietHoursStart = request.QuietHoursStart,
            QuietHoursEnd = request.QuietHoursEnd,
            TimeZone = Trim(request.TimeZone) ?? "UTC",
            DigestQuietHours = request.DigestQuietHours,
            IsActive = request.IsActive,
            CreatedBy = actorId,
            CreatedAt = now,
            UpdatedBy = actorId,
            UpdatedAt = now,
        };
        dbContext.NotificationRoutingRules.Add(rule);
        await dbContext.SaveChangesAsync(cancellationToken);

        var response = Map(rule);
        await auditService.WriteAsync(
            actor, "Created", "NotificationRoutingRule", rule.Id.ToString(), null, response, cancellationToken);
        return new(NotificationOutcome.Success, response);
    }

    public async Task<NotificationRoutingRuleResult> UpdateRuleAsync(
        Guid id,
        SaveNotificationRoutingRuleRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var rule = await dbContext.NotificationRoutingRules
            .Include(item => item.Channel)
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (rule is null)
        {
            return new(NotificationOutcome.NotFound);
        }

        var channel = rule.ChannelId == request.ChannelId
            ? rule.Channel
            : await dbContext.NotificationChannels
                .SingleOrDefaultAsync(item => item.Id == request.ChannelId, cancellationToken);
        if (ValidateRule(request, channel) is { Count: > 0 } errors)
        {
            return new(NotificationOutcome.Invalid, Errors: errors);
        }

        var name = request.Name.Trim();
        if (await dbContext.NotificationRoutingRules
                .AnyAsync(item => item.Name == name && item.Id != id, cancellationToken))
        {
            return new(NotificationOutcome.Conflict, Error: $"A routing rule named '{name}' already exists.");
        }

        var before = Map(rule);
        rule.Name = name;
        rule.ChannelId = request.ChannelId;
        rule.Channel = channel!;
        rule.EventKind = Trim(request.EventKind);
        rule.MinimumSeverity = request.MinimumSeverity;
        rule.DeviceGroup = Trim(request.DeviceGroup);
        rule.QuietHoursStart = request.QuietHoursStart;
        rule.QuietHoursEnd = request.QuietHoursEnd;
        rule.TimeZone = Trim(request.TimeZone) ?? "UTC";
        rule.DigestQuietHours = request.DigestQuietHours;
        rule.IsActive = request.IsActive;
        rule.UpdatedBy = ActorId(actor);
        rule.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);

        var response = Map(rule);
        await auditService.WriteAsync(
            actor, "Updated", "NotificationRoutingRule", id.ToString(), before, response, cancellationToken);
        return new(NotificationOutcome.Success, response);
    }

    public async Task<NotificationOutcome> DeleteRuleAsync(
        Guid id,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        var rule = await dbContext.NotificationRoutingRules
            .Include(item => item.Channel)
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (rule is null)
        {
            return NotificationOutcome.NotFound;
        }

        var before = Map(rule);
        dbContext.NotificationRoutingRules.Remove(rule);
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditService.WriteAsync(
            actor, "Deleted", "NotificationRoutingRule", id.ToString(), before, null, cancellationToken);
        return NotificationOutcome.Success;
    }

    public async Task<UserNotificationPreferenceResponse> GetPreferenceAsync(
        string userId,
        CancellationToken cancellationToken)
    {
        var preference = await dbContext.UserNotificationPreferences.AsNoTracking()
            .SingleOrDefaultAsync(item => item.UserId == userId, cancellationToken);
        return preference is null
            ? Map(NotificationRouter.DefaultPreference(userId), configured: false)
            : Map(preference, configured: true);
    }

    public async Task<UserNotificationPreferenceResult> SavePreferenceAsync(
        SaveUserNotificationPreferenceRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (request.EmailAddress is { Length: > 0 } address && !MailAddress.TryCreate(address, out _))
        {
            errors["EmailAddress"] = ["Enter a valid email address, or leave it blank to use the directory's."];
        }

        ValidateSchedule(request.QuietHoursStart, request.QuietHoursEnd, request.TimeZone, errors);
        if (errors.Count > 0)
        {
            return new(NotificationOutcome.Invalid, Errors: errors);
        }

        var userId = ActorId(actor);
        var preference = await dbContext.UserNotificationPreferences
            .SingleOrDefaultAsync(item => item.UserId == userId, cancellationToken);
        var before = preference is null ? null : Map(preference, configured: true);
        if (preference is null)
        {
            preference = new UserNotificationPreference
            {
                Id = Guid.CreateVersion7(),
                UserId = userId,
                TimeZone = "UTC",
            };
            dbContext.UserNotificationPreferences.Add(preference);
        }

        preference.EmailAddress = Trim(request.EmailAddress);
        preference.EmailEnabled = request.EmailEnabled;
        preference.MinimumSeverity = request.MinimumSeverity;
        preference.QuietHoursStart = request.QuietHoursStart;
        preference.QuietHoursEnd = request.QuietHoursEnd;
        preference.TimeZone = Trim(request.TimeZone) ?? "UTC";
        preference.DigestQuietHours = request.DigestQuietHours;
        preference.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);

        var response = Map(preference, configured: true);
        await auditService.WriteAsync(
            actor, "Saved", "UserNotificationPreference", userId, before, response, cancellationToken);
        return new(NotificationOutcome.Success, response);
    }

    public async Task<NotificationDeliveryPageResponse> ListDeliveriesAsync(
        NotificationDeliveryListRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var page = Math.Max(request.Page, 1);
        var pageSize = Math.Clamp(request.PageSize, 1, MaximumPageSize);

        var query = dbContext.NotificationDeliveries.AsNoTracking().Include(item => item.Channel).AsQueryable();
        if (!string.IsNullOrWhiteSpace(request.EventKind))
        {
            var kind = request.EventKind.Trim();
            query = query.Where(item => item.EventKind == kind);
        }

        if (request.Outcome is { } outcome)
        {
            query = query.Where(item => item.Outcome == outcome);
        }

        if (request.ChannelId is { } channelId)
        {
            query = query.Where(item => item.ChannelId == channelId);
        }

        if (!string.IsNullOrWhiteSpace(request.UserId))
        {
            var userId = request.UserId.Trim();
            query = query.Where(item => item.UserId == userId);
        }

        var total = await query.CountAsync(cancellationToken);
        var deliveries = await query
            .OrderByDescending(item => item.OccurredAt).ThenBy(item => item.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
        return new([.. deliveries.Select(Map)], total, page, pageSize);
    }

    private static IReadOnlyDictionary<string, string[]> ValidateTarget(NotificationChannelKind kind, string target)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var value = target.Trim();
        if (kind is NotificationChannelKind.Email)
        {
            if (!MailAddress.TryCreate(value, out _))
            {
                errors["Target"] = ["An email channel's target must be an email address."];
            }

            return errors;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            errors["Target"] = [$"A {kind} channel's target must be an absolute http(s) webhook URL."];
        }

        return errors;
    }

    private static IReadOnlyDictionary<string, string[]> ValidateRule(
        SaveNotificationRoutingRuleRequest request,
        NotificationChannel? channel)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (channel is null)
        {
            errors["ChannelId"] = ["Unknown notification channel."];
        }

        ValidateSchedule(request.QuietHoursStart, request.QuietHoursEnd, request.TimeZone, errors);
        return errors;
    }

    private static void ValidateSchedule(
        TimeOnly? start,
        TimeOnly? end,
        string? timeZone,
        Dictionary<string, string[]> errors)
    {
        // Half a window is not a window, and a platform that guessed the other end would silence
        // notifications nobody asked to silence.
        if (start is null != end is null)
        {
            errors["QuietHoursStart"] = ["Quiet hours need both a start and an end, or neither."];
        }
        else if (start is not null && start == end)
        {
            errors["QuietHoursStart"] = ["Quiet hours that start and end at the same time cover nothing."];
        }

        if (!string.IsNullOrWhiteSpace(timeZone) && !QuietHoursSchedule.IsKnownZone(timeZone))
        {
            errors["TimeZone"] = [$"'{timeZone}' is not a known time zone. Use an IANA id such as 'Europe/London'."];
        }
    }

    private static NotificationChannelResponse Map(NotificationChannel channel) => new(
        channel.Id,
        channel.Name,
        channel.Kind,
        NotificationChannel.Redact(channel.Kind, channel.Target),
        channel.Description,
        channel.IsActive,
        channel.Rules.Count,
        channel.CreatedBy,
        channel.CreatedAt,
        channel.UpdatedBy,
        channel.UpdatedAt);

    private static NotificationRoutingRuleResponse Map(NotificationRoutingRule rule) => new(
        rule.Id,
        rule.Name,
        rule.ChannelId,
        rule.Channel.Name,
        rule.Channel.Kind,
        rule.EventKind,
        rule.MinimumSeverity,
        rule.DeviceGroup,
        rule.QuietHoursStart,
        rule.QuietHoursEnd,
        rule.TimeZone,
        rule.DigestQuietHours,
        rule.IsActive,
        rule.CreatedBy,
        rule.CreatedAt,
        rule.UpdatedBy,
        rule.UpdatedAt);

    private static UserNotificationPreferenceResponse Map(UserNotificationPreference preference, bool configured) => new(
        preference.UserId,
        preference.EmailAddress,
        preference.EmailEnabled,
        preference.MinimumSeverity,
        preference.QuietHoursStart,
        preference.QuietHoursEnd,
        preference.TimeZone,
        preference.DigestQuietHours,
        configured,
        configured ? preference.UpdatedAt : null);

    private static NotificationDeliveryResponse Map(NotificationDelivery delivery) => new(
        delivery.Id,
        delivery.EventKind,
        delivery.Severity,
        delivery.Subject,
        delivery.DeepLink,
        delivery.DedupeKey,
        delivery.ChannelId,
        delivery.Channel?.Name,
        delivery.ChannelKind,
        delivery.TargetRedacted,
        delivery.UserId,
        delivery.RuleId,
        delivery.Outcome,
        delivery.Detail,
        delivery.ReleaseAfter,
        delivery.DigestDeliveryId,
        delivery.DigestOfCount,
        delivery.OccurredAt,
        delivery.CompletedAt);

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string ActorId(ClaimsPrincipal actor) =>
        actor.FindFirstValue("sub") ?? actor.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException("An authenticated actor identifier is required.");
}
