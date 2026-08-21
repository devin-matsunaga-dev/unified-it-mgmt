using System.Security.Claims;

using Microsoft.EntityFrameworkCore;

using Modules.Assets.Data;
using Platform.Auditing;

namespace Modules.Assets.Features.Contracts;

public sealed record ContractReminderSettingsResponse(
    IReadOnlyList<int> ThresholdDays,
    bool Enabled,
    IReadOnlyList<string> Recipients,
    string UpdatedBy,
    DateTimeOffset UpdatedAt);

public sealed record SaveContractReminderSettingsRequest(
    IReadOnlyList<int> ThresholdDays,
    bool Enabled = true,
    IReadOnlyList<string>? Recipients = null);

public interface IContractReminderSettingsService
{
    Task<ContractReminderSettingsResponse> GetAsync(CancellationToken cancellationToken);

    Task<ContractReminderSettingsResponse> SaveAsync(
        SaveContractReminderSettingsRequest request, ClaimsPrincipal actor, CancellationToken cancellationToken);

    /// <summary>The thresholds the notification job should use on this run.</summary>
    Task<IReadOnlyList<int>> ThresholdsAsync(CancellationToken cancellationToken);

    /// <summary>Who every contract notice goes to, or empty to fall back to each contract's owner.</summary>
    Task<IReadOnlyList<string>> RecipientsAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Reads and writes the single row that says how far ahead renewal notices go out.
/// <para>
/// The row is created on first write rather than seeded, so a database that has never had the screen
/// opened still behaves exactly as it did before this existed — the defaults live in code and the
/// table is empty until somebody changes something.
/// </para>
/// </summary>
public sealed class ContractReminderSettingsService(
    AssetsDbContext dbContext,
    IAuditService auditService) : IContractReminderSettingsService
{
    /// <summary>
    /// A year out is the furthest a renewal notice makes sense; beyond it the job would scan the whole
    /// estate every night to tell somebody about a contract nobody can act on yet.
    /// </summary>
    public const int MaximumThresholdDays = 365;

    /// <summary>More than this and the owner stops reading them, which is worse than too few.</summary>
    public const int MaximumThresholds = 6;

    /// <summary>
    /// Enough for a team mailbox and a few individuals. The ceiling exists because every address is
    /// joined into one <c>ContractNotification.Recipient</c> column, and a list is easier to keep short
    /// than to keep unbounded — a wider audience belongs behind a distribution group, not here.
    /// </summary>
    public const int MaximumRecipients = 5;

    /// <summary>The practical limit on an address; matches the column contract notices are recorded in.</summary>
    public const int MaximumRecipientLength = 254;

    public async Task<ContractReminderSettingsResponse> GetAsync(CancellationToken cancellationToken)
    {
        var settings = await LoadAsync(cancellationToken);
        return Map(settings);
    }

    public async Task<IReadOnlyList<int>> ThresholdsAsync(CancellationToken cancellationToken)
    {
        var settings = await dbContext.ContractReminderSettings.AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == Data.ContractReminderSettings.SingletonId, cancellationToken);
        // Disabled means no thresholds at all, which the planner reads as "nothing is due" — a
        // quieter switch than teaching every caller about an enabled flag.
        if (settings is not null && !settings.Enabled) return [];
        return settings?.ThresholdDays ?? Data.ContractReminderSettings.DefaultThresholdDays;
    }

    public async Task<IReadOnlyList<string>> RecipientsAsync(CancellationToken cancellationToken)
    {
        var settings = await dbContext.ContractReminderSettings.AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == Data.ContractReminderSettings.SingletonId, cancellationToken);
        return settings?.Recipients ?? [];
    }

    public async Task<ContractReminderSettingsResponse> SaveAsync(
        SaveContractReminderSettingsRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Sorted widest-first and de-duplicated: the planner takes the tightest threshold a due date
        // has crossed, and a repeated number would be a second notice for one day.
        var thresholds = request.ThresholdDays
            .Distinct()
            .OrderByDescending(days => days)
            .ToArray();

        var existing = await dbContext.ContractReminderSettings
            .SingleOrDefaultAsync(row => row.Id == Data.ContractReminderSettings.SingletonId, cancellationToken);
        var before = existing is null ? null : Map(existing);
        var settings = existing ?? new Data.ContractReminderSettings
        {
            Id = Data.ContractReminderSettings.SingletonId,
            UpdatedBy = string.Empty,
        };

        settings.ThresholdDays = thresholds;
        settings.Enabled = request.Enabled;
        // Trimmed, lower-cased and de-duplicated: an address list a person edits by hand collects
        // stray spacing and the same mailbox twice, and either would send somebody two of everything.
        settings.Recipients = (request.Recipients ?? [])
            .Select(address => address.Trim().ToLowerInvariant())
            .Where(address => address.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        settings.UpdatedBy = actor.FindFirstValue("preferred_username") ?? actor.FindFirstValue("sub") ?? "unknown";
        settings.UpdatedAt = DateTimeOffset.UtcNow;
        if (existing is null) dbContext.ContractReminderSettings.Add(settings);
        await dbContext.SaveChangesAsync(cancellationToken);

        var response = Map(settings);
        await auditService.WriteAsync(
            actor, "Updated", "ContractReminderSettings", settings.Id.ToString(), before, response, cancellationToken);
        return response;
    }

    private async Task<Data.ContractReminderSettings> LoadAsync(CancellationToken cancellationToken) =>
        await dbContext.ContractReminderSettings.AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == Data.ContractReminderSettings.SingletonId, cancellationToken)
        ?? new Data.ContractReminderSettings
        {
            UpdatedBy = "default",
            UpdatedAt = DateTimeOffset.MinValue,
        };

    private static ContractReminderSettingsResponse Map(Data.ContractReminderSettings settings) =>
        new(settings.ThresholdDays, settings.Enabled, settings.Recipients, settings.UpdatedBy, settings.UpdatedAt);
}
