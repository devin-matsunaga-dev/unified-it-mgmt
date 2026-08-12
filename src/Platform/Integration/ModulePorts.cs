namespace Platform.Integration;

/// <summary>
/// The read a module needs of another module's records. A port lives here rather than in the owning
/// module because two modules that read each other cannot both hold a project reference to the other;
/// the owning module implements its own port and nobody queries a schema they do not own.
/// </summary>
public interface ICiDirectory
{
    /// <summary>The CIs among <paramref name="ids"/> that exist. Unknown ids are simply absent.</summary>
    Task<IReadOnlyList<CiSummary>> GetSummariesAsync(
        IReadOnlyCollection<Guid> ids,
        CancellationToken cancellationToken);
}

/// <summary>
/// Enough of a CI to render a card next to a ticket or an alert. Type, lifecycle state and warranty
/// status are strings so the port stays free of the Assets module's enums.
/// <para>
/// Everything here is read live at request time and nothing is snapshotted, following WP-2.4's rule:
/// a CI cannot leave the module, the delete guard keeps it resolvable, and a renamed owner or a
/// renewed warranty must reach every ticket and every alert at once rather than per record.
/// </para>
/// </summary>
/// <param name="WarrantyStatus">
/// <c>Active</c>, <c>ExpiringSoon</c> or <c>Expired</c>; null where no warranty date is recorded.
/// </param>
/// <param name="WarrantyDaysRemaining">
/// Negative once the warranty has expired, which is how "expired 40 days ago" is said.
/// </param>
public sealed record CiSummary(
    Guid Id,
    string Type,
    string Name,
    string? AssetTag,
    string? SerialNumber,
    string LifecycleState,
    bool IsActive,
    string? OwnerName,
    string? SiteName,
    string? DepartmentName = null,
    DateOnly? WarrantyExpiresAt = null,
    string? WarrantyStatus = null,
    int? WarrantyDaysRemaining = null,
    string? ContractName = null);

/// <summary>
/// The Helpdesk side of the same arrangement: Assets asks whether a CI is still spoken for by a ticket
/// before it lets anyone delete it, and Monitoring asks what is already being worked on for the CI an
/// alert names.
/// </summary>
public interface ITicketLinkDirectory
{
    Task<int> CountLinksForCiAsync(Guid ciId, CancellationToken cancellationToken);

    /// <summary>
    /// The unfinished tickets linked to this CI, newest first, capped at <paramref name="limit"/>.
    /// "Open" means anything not Resolved or Closed — a ticket somebody would still be working.
    /// </summary>
    Task<IReadOnlyList<LinkedTicketSummary>> GetOpenTicketsForCiAsync(
        Guid ciId,
        int limit,
        CancellationToken cancellationToken);
}

/// <summary>
/// The same arrangement pointing the other way, and the first port Platform itself is the caller of:
/// the credential vault asks whether anything still authenticates with a credential before it lets
/// anybody delete it. Platform may not query <c>monitoring.check_definitions</c>, and Monitoring must
/// not be the place the vault's delete guard lives, so the read surface is here and Monitoring
/// implements it.
/// <para>
/// Read-only and narrow, per ARCHITECTURE §3: a port is never a write path. Nothing here can change a
/// check, and nothing here answers with a credential.
/// </para>
/// </summary>
public interface ICredentialUsageDirectory
{
    /// <summary>How many check definitions name this credential, enabled or not.</summary>
    Task<int> CountChecksUsingCredentialAsync(Guid credentialId, CancellationToken cancellationToken);
}

/// <summary>
/// The answer a host with no Monitoring module gives: nothing uses any credential.
/// <para>
/// Registered by <c>AddPlatformServices</c> with <c>TryAdd</c> so that a seeder, a worker or a test
/// host can construct the vault, and replaced by Monitoring's real implementation wherever that module
/// is registered. The failure mode this avoids is a DI exception at start-up in every host that has a
/// vault but no devices; the failure mode it accepts is that a host wired without Monitoring would let
/// a credential in use be deleted, which is why the real one is registered beside the module rather
/// than opted into.
/// </para>
/// </summary>
public sealed class NoCredentialUsageDirectory : ICredentialUsageDirectory
{
    public Task<int> CountChecksUsingCredentialAsync(Guid credentialId, CancellationToken cancellationToken) =>
        Task.FromResult(0);
}

/// <summary>Enough of a ticket to say "this is already being worked on" beside an alert or another ticket.</summary>
public sealed record LinkedTicketSummary(
    Guid TicketId,
    string Number,
    string Title,
    string Status,
    string Priority,
    DateTimeOffset CreatedAt);
