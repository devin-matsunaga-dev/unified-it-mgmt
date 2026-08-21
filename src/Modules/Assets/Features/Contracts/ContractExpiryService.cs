using System.Globalization;

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
    IContractReminderSettingsService reminderSettings,
    ILogger<ContractExpiryService> logger) : IContractExpiryService
{
    public const string FallbackRecipientKey = "Assets:ContractNoticeRecipient";
    public const string DefaultFallbackRecipient = "it-assets@it-platform.local";

    /// <summary>Separates addresses in a notice's recorded recipient, and how they are split to send.</summary>
    public const char RecipientSeparator = ';';

    private static readonly NotificationTemplate Template = new(
        "ContractExpiry",
        "Renewal notice: {{SubjectName}}",
        "{{Message}}\n\nRaised by the IT platform asset module.");

    public async Task<ContractExpiryRunResponse> RunAsync(CancellationToken cancellationToken)
    {
        var today = ContractExpiryCalculator.Today();
        var fallbackRecipient = configuration[FallbackRecipientKey] ?? DefaultFallbackRecipient;
        // Read per run rather than cached: an administrator changing the thresholds should see the
        // next night's notices follow, not the next restart.
        var thresholds = await reminderSettings.ThresholdsAsync(cancellationToken);
        var configuredRecipients = await reminderSettings.RecipientsAsync(cancellationToken);
        if (thresholds.Count == 0)
        {
            // Notices are switched off. Nothing is scanned and nothing is recorded, so switching them
            // back on later resumes from the same due dates rather than replaying a silent period.
            return new ContractExpiryRunResponse(today, 0, 0, []);
        }

        var horizon = today.AddDays(thresholds.Max());

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

        // Licence pools joined this pass in WP-4.4. A pool is a dated agreement like any other, and its
        // renewal notice is the same 30/7/0 rule; a perpetual pool has no end date and is never a
        // candidate. An inactive one is out of the estate, exactly as a retired asset's warranty is.
        var licensePools = await dbContext.LicensePools
            .Include(pool => pool.Product)
            .Where(pool => pool.IsActive && pool.ExpiresAt != null && pool.ExpiresAt <= horizon)
            .Select(pool => new
            {
                pool.Id,
                pool.Name,
                ProductName = pool.Product.Name,
                Publisher = pool.Product.Publisher,
                pool.Entitlements,
                ExpiresAt = pool.ExpiresAt!.Value,
            })
            .ToListAsync(cancellationToken);

        var candidates = new List<ContractExpiryCandidate>(
            contracts.Count + warranties.Count + licensePools.Count);
        // Renewals are a team's job, not a named owner's, so a configured list wins outright here.
        // With none set the behaviour is what it was: the owner, then the asset mailbox.
        var contractRecipient = configuredRecipients.Count > 0
            ? string.Join(RecipientSeparator, configuredRecipients)
            : null;
        candidates.AddRange(contracts.Select(contract => new ContractExpiryCandidate(
            ContractNotificationSubject.Contract,
            contract.Id,
            $"{contract.Type} contract {contract.PoNumber} ({contract.Name}, {contract.Vendor.Name})",
            contract.EndDate,
            contractRecipient ?? Recipient(contract.OwnerEmail, fallbackRecipient),
            ContractDetails(contract))));
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

        // A pool has no personal holder — nobody's laptop is the licence — so every licence notice goes
        // to the asset mailbox.
        candidates.AddRange(licensePools.Select(pool => new ContractExpiryCandidate(
            ContractNotificationSubject.License,
            pool.Id,
            $"{pool.Entitlements}-seat licence for {pool.Publisher} {pool.ProductName} ({pool.Name})",
            pool.ExpiresAt,
            fallbackRecipient)));

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

        var notices = ContractExpiryPlanner.Plan(candidates, today, alreadySent, thresholds);
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
            // One message per address rather than one with several in the To line: the notification
            // port takes a single recipient, and a failure to one mailbox should not lose the rest.
            foreach (var recipient in notification.Recipient.Split(
                         RecipientSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                try
                {
                    await notificationService.SendAsync(
                        new NotificationMessage(recipient, Template, notification),
                        cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // An unreachable relay must not take the rest of the batch with it, nor fail the
                    // request that asked for the pass — the notices are already recorded, and a run
                    // that half-sent and then threw would look to an operator like nothing happened.
                    // The row survives and the dedupe key stands, so this notice is not retried: the
                    // warning below is the only trace, which is why it names the mailbox.
                    logger.LogWarning(
                        exception,
                        "Contract expiry notice {SubjectId} at {ThresholdDays} days could not be mailed to {Recipient}.",
                        notification.SubjectId, notification.ThresholdDays, recipient);
                }
            }
        }

        return new(today, contracts.Count, warranties.Count, raised, licensePools.Count);
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

    /// <summary>
    /// The facts that decide what to do about a renewal, on one line: who it is with, who it belongs
    /// to, and what it costs. Anything absent is left out rather than printed empty, so a sparsely
    /// filled contract produces a short line instead of a row of dashes.
    /// </summary>
    private static string ContractDetails(Contract contract)
    {
        var parts = new List<string>(4) { $"Vendor: {contract.Vendor.Name}" };
        if (!string.IsNullOrWhiteSpace(contract.DepartmentName)) parts.Add($"Department: {contract.DepartmentName}");
        if (contract.Cost is { } cost)
        {
            var currency = string.IsNullOrWhiteSpace(contract.Currency) ? string.Empty : $" {contract.Currency}";
            parts.Add($"Cost: {cost.ToString("N2", CultureInfo.InvariantCulture)}{currency}");
        }

        var owner = contract.OwnerName ?? contract.OwnerEmail;
        if (!string.IsNullOrWhiteSpace(owner)) parts.Add($"Owner: {owner}");
        return string.Join(" · ", parts);
    }

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
