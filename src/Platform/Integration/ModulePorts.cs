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

/// <summary>
/// Which CI, if any, is already polled at an address — the read Assets makes of Monitoring while
/// placing a discovered device (WP-4.2).
/// <para>
/// It is the strongest rung of the match ladder and the only one that is not a heuristic: a monitored
/// device exists because an operator created it and named the CI it is, so "this address is that CI" is
/// a decision already taken rather than an inference from a naming convention. Assets may not query
/// <c>monitoring.monitored_devices</c>, and Monitoring must not be where the CMDB's matching lives, so
/// the read surface is here and Monitoring implements it — the same arrangement as
/// <see cref="ICredentialUsageDirectory"/>, pointing the other way.
/// </para>
/// <para>
/// Read-only and narrow, per ARCHITECTURE §3. Approving a discovery <em>into</em> monitoring is a write
/// and therefore travels as an event instead; a port is never a write path.
/// </para>
/// </summary>
public interface IMonitoredAddressDirectory
{
    /// <summary>
    /// The CI monitored at any of <paramref name="addresses"/>, or null when none is. Takes the whole
    /// candidate list — an address and a hostname for one discovery — because that is one query rather
    /// than one per spelling of the same device.
    /// <para>
    /// Answers null rather than picking when two devices match different candidates: that is two CIs
    /// claiming one discovery, which is a question for a human and not a tie this port may break.
    /// </para>
    /// </summary>
    Task<Guid?> FindCiByAddressAsync(
        IReadOnlyCollection<string> addresses,
        CancellationToken cancellationToken);
}

/// <summary>
/// The answer a host with no Monitoring module gives: nothing is monitored at any address.
/// <para>
/// Registered by <c>AddPlatformServices</c> with <c>TryAdd</c>, following
/// <see cref="NoCredentialUsageDirectory"/>, so a seeder or a test host can construct the Assets module
/// without Monitoring. The cost is bounded and worth stating: such a host still matches discoveries,
/// just one rung down the ladder — it can never wrongly match, only queue for review something the top
/// rung would have placed.
/// </para>
/// </summary>
public sealed class NoMonitoredAddressDirectory : IMonitoredAddressDirectory
{
    public Task<Guid?> FindCiByAddressAsync(
        IReadOnlyCollection<string> addresses,
        CancellationToken cancellationToken) => Task.FromResult<Guid?>(null);
}

/// <summary>
/// Which of these CIs depends on which — the read Monitoring makes of the CMDB while deciding whether
/// an alert is a cause or a consequence (WP-5.1).
/// <para>
/// Narrow on purpose. It answers only about CIs the caller already named, never "what depends on this",
/// because the correlation question is always asked about a set that is already alerting: a walk that
/// returned the whole subtree would hand the alert engine an estate-sized answer to a question about
/// six devices. Monitoring may not query <c>assets.ci_relationships</c>, and Assets must not be where
/// alert correlation lives, so the read surface is here and Assets implements it.
/// </para>
/// <para>
/// Read-only and narrow, per ARCHITECTURE §3. Nothing here creates or confirms an edge — a correlation
/// is a reading of the graph an operator asserted, and WP-4.3 is emphatic that nothing derived from
/// monitoring may write one.
/// </para>
/// </summary>
public interface ICiDependencyDirectory
{
    /// <summary>
    /// For each of <paramref name="ciIds"/>, the others in that same set it transitively depends on.
    /// Direction follows WP-2.3: a CI depends on what it needs, so <c>DependsOnCiId</c> is an ancestor
    /// of <c>CiId</c> and its failure is the one that would explain the other's.
    /// <para>
    /// Both ends are always inside <paramref name="ciIds"/>. A dependency that is perfectly healthy is
    /// not part of the answer, because the caller only asked about things that are currently bad.
    /// </para>
    /// </summary>
    /// <param name="maxDepth">
    /// Hops to walk, clamped by the implementation to WP-2.3's ceiling. A chain longer than this is
    /// reported as far as it was walked rather than truncated silently — the near end is still correct.
    /// </param>
    Task<IReadOnlyList<CiDependencyLink>> GetDependenciesAmongAsync(
        IReadOnlyCollection<Guid> ciIds,
        int maxDepth,
        CancellationToken cancellationToken);
}

/// <summary>
/// "<paramref name="CiId"/> needs <paramref name="DependsOnCiId"/>, and it is <paramref name="Depth"/>
/// hops away." The fewest hops, where several routes exist.
/// </summary>
public sealed record CiDependencyLink(Guid CiId, Guid DependsOnCiId, int Depth);

/// <summary>
/// The answer a host with no Assets module gives: nothing depends on anything.
/// <para>
/// Registered by <c>AddPlatformServices</c> with <c>TryAdd</c>, following
/// <see cref="NoMonitoredAddressDirectory"/>. The degradation is worth stating because it is the safe
/// direction and not merely a convenience: with no dependency graph, no alert is ever suppressed and
/// every one of them publishes exactly as it did before WP-5.1. A correlation engine that cannot read
/// the CMDB must fall back to telling somebody about everything, never to silence.
/// </para>
/// </summary>
public sealed class NoCiDependencyDirectory : ICiDependencyDirectory
{
    public Task<IReadOnlyList<CiDependencyLink>> GetDependenciesAmongAsync(
        IReadOnlyCollection<Guid> ciIds,
        int maxDepth,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<CiDependencyLink>>([]);
}

/// <summary>
/// What else is failing because of this alert — the read Helpdesk makes of Monitoring while composing
/// a root-cause ticket (WP-5.1).
/// <para>
/// It exists so the ticket can list the CIs an outage took with it without <c>AlertRaised</c> growing a
/// member. CONVENTIONS versions an event by a new type rather than by mutation, and a list that is
/// already committed to the alert rows before the event is published is a read, not a payload. The
/// ordering that makes it safe is WP-3.5's: the engine commits every alert row before it publishes
/// anything, so a consumer of <c>AlertRaised</c> asking this question can never see a correlation the
/// database does not yet hold.
/// </para>
/// </summary>
public interface IAlertCorrelationDirectory
{
    /// <summary>
    /// The open alerts currently suppressed underneath <paramref name="rootCauseAlertId"/>, oldest
    /// first. Empty for an ordinary alert that explains nothing but itself, which is most of them.
    /// </summary>
    Task<IReadOnlyList<CorrelatedAlertSummary>> GetImpactedByAsync(
        Guid rootCauseAlertId,
        CancellationToken cancellationToken);
}

/// <summary>Enough of a suppressed alert to name it in a ticket: which CI, and what was wrong with it.</summary>
public sealed record CorrelatedAlertSummary(
    Guid AlertId,
    Guid CiId,
    Guid DeviceId,
    string RuleId,
    string Severity,
    string Summary);

/// <summary>
/// The answer a host with no Monitoring module gives: this alert explains nothing.
/// <para>
/// Registered by <c>AddPlatformServices</c> with <c>TryAdd</c>, following
/// <see cref="NoCredentialUsageDirectory"/>. A root-cause ticket in such a host is an ordinary alert
/// ticket, which is what it was before this package.
/// </para>
/// </summary>
public sealed class NoAlertCorrelationDirectory : IAlertCorrelationDirectory
{
    public Task<IReadOnlyList<CorrelatedAlertSummary>> GetImpactedByAsync(
        Guid rootCauseAlertId,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<CorrelatedAlertSummary>>([]);
}

/// <summary>Enough of a ticket to say "this is already being worked on" beside an alert or another ticket.</summary>
public sealed record LinkedTicketSummary(
    Guid TicketId,
    string Number,
    string Title,
    string Status,
    string Priority,
    DateTimeOffset CreatedAt);

/// <summary>
/// What is already on the clock across a set of CIs — the read Assets makes of Helpdesk while working
/// out a blast radius (WP-5.2).
/// <para>
/// A second port rather than a third method on <see cref="ITicketLinkDirectory"/>, following WP-5.1's
/// call: that one answers "is anything open on this one CI", asked while deleting a CI or rendering a
/// card, and it is deliberately per-CI. This one is asked about a whole outage at once and carries the
/// SLA clock, which is a join and an arithmetic <see cref="LinkedTicketSummary"/> has no room for.
/// Widening the older port would put that cost on every caller of it, and there are four.
/// </para>
/// <para>
/// Read-only and narrow, per ARCHITECTURE §3. Nothing here changes a ticket or its SLA — a blast radius
/// is a reading of what an outage would cost, never a decision about it.
/// </para>
/// </summary>
public interface ITicketImpactDirectory
{
    /// <summary>
    /// The unfinished tickets linked to any of <paramref name="ciIds"/>, worst SLA exposure first, capped
    /// at <paramref name="limit"/>. "Open" follows <see cref="ITicketLinkDirectory"/>: anything not
    /// Resolved or Closed.
    /// <para>
    /// One statement for the whole set rather than a query per CI. A blast radius is largest exactly when
    /// the estate is worst, which is the wrong moment to make the answer cost one round trip per CI.
    /// </para>
    /// </summary>
    /// <returns>
    /// The tickets to render and how many there are in total. The two differ once an outage carries more
    /// open work than the panel will list, and the caller states the honest number — WP-2.4's rule that a
    /// truncated answer must never look like a complete one.
    /// </returns>
    Task<ImpactedTicketSet> GetOpenTicketsForCisAsync(
        IReadOnlyCollection<Guid> ciIds,
        int limit,
        CancellationToken cancellationToken);
}

/// <summary>The tickets a blast radius carries, and the true total behind them.</summary>
public sealed record ImpactedTicketSet(IReadOnlyList<ImpactedTicketSummary> Tickets, int Total);

/// <summary>
/// One open ticket sitting on a CI inside the blast radius, with the CI it is linked to so the caller
/// can attribute it without a second lookup.
/// </summary>
/// <param name="Sla">
/// Null when no SLA policy matched the ticket's priority when it was raised, which is a real state
/// rather than a missing read: nothing is on the clock, and the panel must say so rather than imply a
/// deadline that does not exist.
/// </param>
public sealed record ImpactedTicketSummary(
    Guid TicketId,
    Guid CiId,
    string Number,
    string Title,
    string Status,
    string Priority,
    DateTimeOffset CreatedAt,
    SlaExposure? Sla);

/// <summary>
/// Where one ticket stands against its resolution target, measured now.
/// <para>
/// Computed from the SLA clock at read time rather than read off the stored breach flags, because those
/// are advanced by the scheduler's pass and a blast radius asked between two passes would under-report
/// the exposure. The flags remain the audited record of when a breach was *declared*; this is what the
/// clock says at this instant, which is the same source <c>GET /api/tickets/{id}/sla</c> answers from.
/// </para>
/// </summary>
/// <param name="RemainingSeconds">Business seconds left against the resolution target; zero once breached.</param>
public sealed record SlaExposure(
    string PolicyName,
    DateTimeOffset ResolutionDueAt,
    double RemainingSeconds,
    bool Breached,
    bool AtRisk);

/// <summary>
/// The answer a host with no Helpdesk module gives: nothing is open anywhere.
/// <para>
/// Registered by <c>AddPlatformServices</c> with <c>TryAdd</c>, following
/// <see cref="NoCiDependencyDirectory"/>. The degradation is the honest one for this read: a blast
/// radius still names every CI an outage would take with it, and reports no tickets and no SLA exposure
/// rather than refusing to answer. It under-states the cost of the outage and can never over-state it.
/// </para>
/// </summary>
public sealed class NoTicketImpactDirectory : ITicketImpactDirectory
{
    public Task<ImpactedTicketSet> GetOpenTicketsForCisAsync(
        IReadOnlyCollection<Guid> ciIds,
        int limit,
        CancellationToken cancellationToken) =>
        Task.FromResult(new ImpactedTicketSet([], 0));
}
