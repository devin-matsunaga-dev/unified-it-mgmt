namespace Platform.Directory;

/// <summary>
/// A department as Settings edits it: the locations it operates at, plus how many people are in it,
/// which is what makes a delete refusal explainable before it is attempted.
/// </summary>
public sealed record DepartmentAdminResponse(
    Guid Id,
    string Code,
    string Name,
    IReadOnlyList<DirectorySite> Sites,
    int UserCount);

/// <summary>A location as Settings edits it, with the departments present there.</summary>
public sealed record SiteAdminResponse(
    Guid Id,
    string Code,
    string Name,
    IReadOnlyList<DirectoryDepartment> Departments,
    int UserCount);

/// <param name="SiteIds">The locations this department operates at. Replaces the set wholesale.</param>
public sealed record SaveDepartmentRequest(string Code, string Name, IReadOnlyList<Guid> SiteIds);

/// <param name="DepartmentIds">The departments present at this location. Replaces the set wholesale.</param>
public sealed record SaveSiteRequest(string Code, string Name, IReadOnlyList<Guid> DepartmentIds);

public enum DirectoryOutcome
{
    Success,
    NotFound,
    DuplicateCode,
    UnknownReference,
    InUse,
}

public sealed record DepartmentAdminResult(
    DirectoryOutcome Outcome,
    DepartmentAdminResponse? Department = null,
    string? Error = null);

public sealed record SiteAdminResult(
    DirectoryOutcome Outcome,
    SiteAdminResponse? Site = null,
    string? Error = null);
