using System.Security.Claims;

using Microsoft.EntityFrameworkCore;
using Modules.Monitoring.Data;
using Modules.Monitoring.Features.Devices;
using Platform.Integration;
using Platform.Vault;

namespace Modules.Monitoring.Features.PollerConfig;

/// <summary>
/// What credentials a poller needs, and the grant that lets it read them.
/// <para>
/// This lives in Monitoring rather than in Platform for one reason: deciding <em>which</em> credentials
/// a poller is entitled to is a monitoring question — it is the distinct set named by enabled checks on
/// enabled devices in that poller's group. Platform mints and releases; Monitoring says who may ask.
/// The poller's own request never names a credential, so a poller cannot widen its own scope by asking
/// for an id it saw somewhere.
/// </para>
/// </summary>
public interface IPollerCredentialService
{
    /// <summary>
    /// The credentials this poller's devices need, with the version each is at. No material, no grant
    /// and no row written — this is the cheap call a poller makes every cycle to notice a rotation.
    /// </summary>
    Task<PollerCredentialScopeResult> GetScopeAsync(string pollerName, CancellationToken cancellationToken);

    /// <summary>
    /// Mints a single-use grant over that same scope. Written as a row, so it is deliberately the call
    /// a poller makes only when it has something to fetch.
    /// </summary>
    Task<PollerCredentialGrantResult> IssueGrantAsync(
        string pollerName, ClaimsPrincipal actor, CancellationToken cancellationToken);
}

public sealed class PollerCredentialService(
    MonitoringDbContext dbContext,
    ICredentialVault credentialVault) : IPollerCredentialService
{
    public async Task<PollerCredentialScopeResult> GetScopeAsync(
        string pollerName,
        CancellationToken cancellationToken)
    {
        var poller = await FindPollerAsync(pollerName, cancellationToken);
        if (poller is null)
        {
            return new(MonitoringOutcome.NotFound);
        }

        var descriptors = await DescribeScopeAsync(poller.PollerGroup, cancellationToken);
        return new(MonitoringOutcome.Success, new PollerCredentialScopeResponse(
            poller.Name, poller.PollerGroup, descriptors));
    }

    public async Task<PollerCredentialGrantResult> IssueGrantAsync(
        string pollerName,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        var poller = await FindPollerAsync(pollerName, cancellationToken);
        if (poller is null)
        {
            return new(MonitoringOutcome.NotFound);
        }

        var descriptors = await DescribeScopeAsync(poller.PollerGroup, cancellationToken);
        if (descriptors.Count == 0)
        {
            // Not an error. An estate where nothing authenticates is the normal state of a fresh
            // install, and a poller that asked would otherwise log a failure every cycle.
            return new(MonitoringOutcome.Success, new CredentialGrantResponse(
                Guid.Empty, string.Empty, DateTimeOffset.UtcNow, []));
        }

        var result = await credentialVault.IssueGrantAsync(
            poller.Name,
            poller.PollerGroup,
            [.. descriptors.Select(descriptor => descriptor.Id)],
            actor,
            cancellationToken);
        return result.Outcome switch
        {
            CredentialOutcome.Success => new(MonitoringOutcome.Success, result.Grant),
            // The vault refuses a scope with nothing active in it, which the check above already
            // handles; anything else reaching here is a scope that went stale between two queries.
            _ => new(MonitoringOutcome.Invalid, Errors: new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["Credentials"] = [result.Error ?? "No credential in this poller's scope can be granted."],
            }),
        };
    }

    /// <summary>
    /// The distinct credentials named by the checks this poller actually runs.
    /// <para>
    /// Filtered to enabled checks on enabled devices, so a credential stops being released the moment
    /// the last check using it is switched off — which is what makes disabling a device an effective
    /// way to stop handing its secret out.
    /// </para>
    /// </summary>
    private async Task<IReadOnlyList<CredentialDescriptor>> DescribeScopeAsync(
        string pollerGroup,
        CancellationToken cancellationToken)
    {
        var credentialIds = await dbContext.CheckDefinitions.AsNoTracking()
            .Where(check => check.CredentialId != null
                && check.IsEnabled
                && check.Device.IsEnabled
                && check.Device.PollerGroup == pollerGroup)
            .Select(check => check.CredentialId!.Value)
            .Distinct()
            .ToListAsync(cancellationToken);

        return credentialIds.Count == 0
            ? []
            : await credentialVault.DescribeAsync(credentialIds, cancellationToken);
    }

    private Task<Poller?> FindPollerAsync(string pollerName, CancellationToken cancellationToken)
    {
        var name = pollerName.Trim();
        return dbContext.Pollers.AsNoTracking()
            .SingleOrDefaultAsync(poller => poller.Name == name, cancellationToken);
    }
}

/// <summary>
/// The Monitoring half of the vault's delete guard. Counts every check naming a credential, enabled or
/// not: a disabled check is one somebody intends to switch back on, and deleting the credential under
/// it would make that a silent failure later rather than a refusal now.
/// </summary>
public sealed class CredentialUsageDirectory(MonitoringDbContext dbContext) : ICredentialUsageDirectory
{
    public Task<int> CountChecksUsingCredentialAsync(Guid credentialId, CancellationToken cancellationToken) =>
        dbContext.CheckDefinitions.AsNoTracking()
            .CountAsync(check => check.CredentialId == credentialId, cancellationToken);
}
