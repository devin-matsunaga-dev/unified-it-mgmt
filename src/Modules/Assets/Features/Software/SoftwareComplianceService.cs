using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using Modules.Assets.Data;
using Modules.Assets.Features.Contracts;

using Platform.Notifications;

using Quartz;

namespace Modules.Assets.Features.Software;

/// <summary>
/// The over-deployment pass: reads the compliance report and records one notice per product installed
/// on more devices than its live pools entitle.
/// <para>
/// It notifies only where a pool exists. A product nobody has bought a licence for is
/// <see cref="SoftwareComplianceState.Unlicensed"/> on the report and stays there — every free browser
/// and driver in the estate is in that state, and mailing about them would bury the one row that means
/// somebody owes money.
/// </para>
/// <para>
/// The notice is filed in <c>assets.contract_notifications</c> beside the renewal notices, which is
/// where an operator already looks. Its dedupe key is (today, size of the overage) rather than a due
/// date, so a steady shortfall is reported at most once a day and a shortfall that grows is reported
/// again the same day.
/// </para>
/// </summary>
public sealed class SoftwareComplianceService(
    AssetsDbContext dbContext,
    ILicensingService licensingService,
    INotificationService notificationService,
    IConfiguration configuration,
    ILogger<SoftwareComplianceService> logger) : ISoftwareComplianceService
{
    private static readonly NotificationTemplate Template = new(
        "SoftwareCompliance",
        "Licence compliance: {{SubjectName}}",
        "{{Message}}\n\nRaised by the IT platform asset module.");

    public async Task<SoftwareComplianceRunResponse> RunAsync(CancellationToken cancellationToken)
    {
        var today = ContractExpiryCalculator.Today();
        var recipient = configuration[ContractExpiryService.FallbackRecipientKey]
            ?? ContractExpiryService.DefaultFallbackRecipient;
        var report = await licensingService.ReportAsync(new(null, null), cancellationToken);
        var breaches = report.Rows.Where(row => row.State == SoftwareComplianceState.OverDeployed).ToList();

        var subjectIds = breaches.Select(row => row.ProductId).ToHashSet();
        var alreadySent = (await dbContext.ContractNotifications
                .Where(notification => notification.Subject == ContractNotificationSubject.LicenseCompliance
                    && subjectIds.Contains(notification.SubjectId)
                    && notification.DueDate == today)
                .Select(notification => new { notification.SubjectId, notification.ThresholdDays })
                .ToListAsync(cancellationToken))
            .Select(row => (row.SubjectId, row.ThresholdDays))
            .ToHashSet();

        var sentAt = DateTimeOffset.UtcNow;
        var raised = new List<ContractNotification>();
        foreach (var breach in breaches)
        {
            if (alreadySent.Contains((breach.ProductId, breach.Overage)))
            {
                continue;
            }

            var notification = new ContractNotification
            {
                Id = Guid.CreateVersion7(),
                Subject = ContractNotificationSubject.LicenseCompliance,
                SubjectId = breach.ProductId,
                SubjectName = Truncate($"{breach.Publisher} {breach.ProductName}", 200),
                DueDate = today,
                ThresholdDays = breach.Overage,
                Recipient = recipient,
                Message = Truncate(
                    $"{breach.Publisher} {breach.ProductName} is installed on {breach.InstalledCiCount} devices "
                    + $"but only {breach.Entitled} are entitled — {breach.Overage} over.",
                    500),
                SentAt = sentAt,
            };
            dbContext.ContractNotifications.Add(notification);
            raised.Add(notification);
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        // Sent after the rows are committed, following the WP-2.6 renewal pass: a failing SMTP host
        // must not replay every notice on the next run.
        var responses = new List<ContractNotificationResponse>(raised.Count);
        foreach (var notification in raised)
        {
            var response = new ContractNotificationResponse(
                notification.Id,
                notification.Subject,
                notification.SubjectId,
                notification.SubjectName,
                notification.DueDate,
                notification.ThresholdDays,
                notification.Recipient,
                notification.Message,
                notification.SentAt);
            responses.Add(response);
            logger.LogWarning(
                "Licence compliance notice for product {ProductId}: {Message}",
                notification.SubjectId, notification.Message);
            await notificationService.SendAsync(
                new NotificationMessage(notification.Recipient, Template, response), cancellationToken);
        }

        return new(today, report.ProductCount, breaches.Count, responses);
    }

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length];
}

/// <summary>
/// Runs the compliance pass once a day. Idempotent within a day, so the trigger firing at host start-up
/// — which is what makes a shortfall visible without waiting until tomorrow — cannot raise it twice.
/// </summary>
[DisallowConcurrentExecution]
public sealed class SoftwareComplianceJob(ISoftwareComplianceService service) : IJob
{
    public Task Execute(IJobExecutionContext context) => service.RunAsync(context.CancellationToken);
}
