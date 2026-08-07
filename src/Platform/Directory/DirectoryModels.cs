namespace Platform.Directory;

/// <summary>A person another module may own a record on behalf of. Never exposes credentials.</summary>
public sealed record DirectoryUser(
    Guid Id,
    string Username,
    string DisplayName,
    string Email,
    string Role,
    Guid SiteId,
    string SiteName,
    Guid DepartmentId,
    string DepartmentName);

public sealed record DirectoryDepartment(Guid Id, string Code, string Name);

/// <summary>A physical location. Stored as a Platform site; "location" in the asset vocabulary.</summary>
public sealed record DirectorySite(Guid Id, string Code, string Name);
