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
/// Enough of a CI to render a card next to a ticket. Type and lifecycle state are strings so the port
/// stays free of the Assets module's enums.
/// </summary>
public sealed record CiSummary(
    Guid Id,
    string Type,
    string Name,
    string? AssetTag,
    string? SerialNumber,
    string LifecycleState,
    bool IsActive,
    string? OwnerName,
    string? SiteName);

/// <summary>
/// The Helpdesk side of the same arrangement: Assets asks whether a CI is still spoken for by a ticket
/// before it lets anyone delete it.
/// </summary>
public interface ITicketLinkDirectory
{
    Task<int> CountLinksForCiAsync(Guid ciId, CancellationToken cancellationToken);
}
