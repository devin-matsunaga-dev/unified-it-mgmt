namespace Platform.Directory;

/// <summary>
/// One module's answer to "is anything of mine still pointing at this department or location?", asked
/// before Settings deletes one.
/// <para>
/// A contribution interface rather than a port, for ARCHITECTURE §3's reason: the question is
/// estate-wide and read-only, every module answers only about its own schema, and Platform holds no
/// reference to any of them — it is handed whatever implementations the host registered. Adding a
/// module's answer is a registration, exactly as it is for <c>ISearchSource</c>.
/// </para>
/// <para>
/// This exists because <c>assets.configuration_items</c> carries a <c>department_id</c> with no foreign
/// key to <c>platform.departments</c> — a cross-schema FK would be the boundary violation this pattern
/// avoids. Without asking, deleting a department would silently leave assets pointing at a dead id.
/// </para>
/// </summary>
public interface IDirectoryUsageSource
{
    /// <summary>
    /// What this source holds, in the plural and lower case, for a refusal a person can act on —
    /// e.g. "configuration items". Read straight into the 409 detail.
    /// </summary>
    string ResourceName { get; }

    Task<int> CountByDepartmentAsync(Guid departmentId, CancellationToken cancellationToken);

    Task<int> CountBySiteAsync(Guid siteId, CancellationToken cancellationToken);
}
