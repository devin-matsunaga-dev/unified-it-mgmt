using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Modules.Assets.Data;
using Platform.Directory;
using Platform.Notifications;

namespace Modules.Assets.Features.Contracts;

/// <summary>
/// The renewal/expiry pass: gathers every contract and CI warranty with an end date, asks
/// <see cref="ContractExpiryPlanner"/> what is due, and records plus sends each notice. The recorded
/// row is the dedupe key, so the run is safe to repeat and safe to trigger by hand.
/// </summary>
public sealed class ContractExpiryService(
    AssetsDbContext dbContext,
    IDirectoryService directoryService,
    INotificationService notificationService,
    IConfiguration configuration,
    ILogger<ContractExpiryService> logger) : IContractExpiryService
{
    public const string FallbackRecipientKey = "Assets:ContractNoticeRecipient";
    public const string DefaultFallbackRecipient = "it-assets@it-platform.local";

    private static readonly NotificationTemplate Template = new(
        "ContractExpiry",
        "Renewal notice: {{SubjectName}}",
        "{{Message}}\n\nRaised by the IT platform asset module.");

    public async Task<ContractExpiryRunResponse> RunAsync(CancellationToken cancellationToken)
    {
        var today = ContractExpiryCalculator.Today();
        var fallbackRecipient = configuration[FallbackRecipientKey] ?? DefaultFallbackRecipient;
        var horizon = today.AddDays(ContractExpiryCalculator.Thresholds.Max());

        var contracts = await dbContext.Contracts
            .Include(contract => contract.Vendor)
            .Where(contract => contract.IsActive && contract.EndDate <= horizon)
            .ToListAsync(cancellationToken);
        // Retired and disposed assets are out of the estate: nobody is going to renew their warranty,
        // so a notice about one is pure noise an operator cannot switch off.
        var warranties = await dbContext.Cis
            .Where(ci => ci.IsActive
                && ci.LifecycleState != CiLifecycleState.Retired
                && ci.LifecycleState != CiLifecycleState.Disposed
                && ci.WarrantyExpiresAt != null && ci.WarrantyExpiresAt <= horizon)
            .Select(ci => new
            {
                ci.Id,
                ci.Name,
                ci.AssetTag,
                WarrantyExpiresAt = ci.WarrantyExpiresAt!.Value,
                OwnerUserId = ci.OwnerUserId,
            })
            .ToListAsync(cancellationToken);

        var candidates = new List<ContractExpiryCandidate>(contracts.Count + warranties.Count);
        candidates.AddRange(contracts.Select(contract => new ContractExpiryCandidate(
            ContractNotificationSubject.Contract,
            contract.Id,
            $"{contract.Type} contract {contract.ContractNumber} ({contract.Name}, {contract.Vendor.Name})",
            contract.EndDate,
            Recipient(contract.OwnerEmail, fallbackRecipient))));
        // A warranty notice goes to whoever holds the asset; an unheld one falls back to the asset
        // mailbox. Owner emails live in the platform directory, so they are resolved through the
        // service rather than read from another module's tables.
        var ownerEmails = new Dictionary<Guid, string?>();
        foreach (var ownerId in warranties.Select(warranty => warranty.OwnerUserId).OfType<Guid>().Distinct())
        {
            ownerEmails[ownerId] = (await directoryService.FindUserAsync(ownerId, cancellationToken))?.Email;
        }

        candidates.AddRange(warranties.Select(warranty => new ContractExpiryCandidate(
            ContractNotificationSubject.Warranty,
            warranty.Id,
            $"Warranty for {warranty.Name}{(warranty.AssetTag is null ? string.Empty : $" ({warranty.AssetTag})")}",
            warranty.WarrantyExpiresAt,
            Recipient(
                warranty.OwnerUserId is { } ownerId && ownerEmails.TryGetValue(ownerId, out var email) ? email : null,
                fallbackRecipient))));

        // Only the notices that could still be due are read back, so the dedupe set stays small even
        // once the table has years of history in it.
        var subjectIds = candidates.Select(candidate => candidate.SubjectId).ToHashSet();
        var alreadySent = (await dbContext.ContractNotifications
                .Where(notification => subjectIds.Contains(notification.SubjectId))
                .Select(notification => new
                {
                    notification.Subject,
                    notification.SubjectId,
                    notification.DueDate,
                    notification.ThresholdDays,
                })
                .ToListAsync(cancellationToken))
            .Select(row => new ContractNotificationKey(row.Subject, row.SubjectId, row.DueDate, row.ThresholdDays))
            .ToHashSet();

        var notices = ContractExpiryPlanner.Plan(candidates, today, alreadySent);
        var raised = new List<ContractNotificationResponse>(notices.Count);
        var sentAt = DateTimeOffset.UtcNow;
        foreach (var notice in notices)
        {
            var notification = new ContractNotification
            {
                Id = Guid.CreateVersion7(),
                Subject = notice.Candidate.Subject,
                SubjectId = notice.Candidate.SubjectId,
                SubjectName = Truncate(notice.Candidate.SubjectName, 200),
                DueDate = notice.Candidate.DueDate,
                ThresholdDays = notice.ThresholdDays,
                Recipient = notice.Candidate.Recipient,
                Message = Truncate(notice.Message, 500),
                SentAt = sentAt,
            };
            dbContext.ContractNotifications.Add(notification);
            raised.Add(Map(notification));
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        // Sending happens after the rows are committed: a mail failure must not replay every notice
        // on the next pass, and the recorded row is what the WP asks to be verifiable.
        foreach (var notification in raised)
        {
            logger.LogInformation(
                "Contract expiry notice for {Subject} {SubjectId} at {ThresholdDays} days: {Message}",
                notification.Subject, notification.SubjectId, notification.ThresholdDays, notification.Message);
            await notificationService.SendAsync(
                new NotificationMessage(notification.Recipient, Template, notification),
                cancellationToken);
        }

        return new(today, contracts.Count, warranties.Count, raised);
    }

    public async Task<IReadOnlyList<ContractNotificationResponse>> ListNotificationsAsync(
        int limit,
        CancellationToken cancellationToken)
    {
        var notifications = await dbContext.ContractNotifications
            .OrderByDescending(notification => notification.SentAt).ThenByDescending(notification => notification.Id)
            .Take(Math.Clamp(limit, 1, 200))
            .ToListAsync(cancellationToken);
        return [.. notifications.Select(Map)];
    }

    private static string Recipient(string? preferred, string fallback) =>
        string.IsNullOrWhiteSpace(preferred) ? fallback : preferred;

    private static string Truncate(string value, int length) =>
        value.Length <= length ? value : value[..length];

    private static ContractNotificationResponse Map(ContractNotification notification) => new(
        notification.Id,
        notification.Subject,
        notification.SubjectId,
        notification.SubjectName,
        notification.DueDate,
        notification.ThresholdDays,
        notification.Recipient,
        notification.Message,
        notification.SentAt);
}
