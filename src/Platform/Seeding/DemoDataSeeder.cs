using Microsoft.EntityFrameworkCore;

using Platform.Data;

namespace Platform.Seeding;

public sealed record SeedResult(int SitesAdded, int DepartmentsAdded, int UsersAdded);

public sealed class DemoDataSeeder(PlatformDbContext dbContext)
{
    private static readonly Site[] Sites =
    [
        new() { Id = Guid.Parse("01980000-0000-7000-8000-000000000001"), Code = "HQ", Name = "Head Office" },
        new() { Id = Guid.Parse("01980000-0000-7000-8000-000000000002"), Code = "DC1", Name = "Primary Data Centre" },
        new() { Id = Guid.Parse("01980000-0000-7000-8000-000000000003"), Code = "BR1", Name = "Regional Branch" },
    ];

    private static readonly Department[] Departments =
    [
        new() { Id = Guid.Parse("01980000-0000-7000-8000-000000000101"), Code = "IT", Name = "Information Technology" },
        new() { Id = Guid.Parse("01980000-0000-7000-8000-000000000102"), Code = "FIN", Name = "Finance" },
        new() { Id = Guid.Parse("01980000-0000-7000-8000-000000000103"), Code = "HR", Name = "People and Culture" },
        new() { Id = Guid.Parse("01980000-0000-7000-8000-000000000104"), Code = "OPS", Name = "Operations" },
    ];

    private static readonly UserSeed[] Users =
    [
        User(1, "admin1", "Admin One", "Admin", "HQ", "IT"),
        User(2, "admin2", "Admin Two", "Admin", "DC1", "IT"),
        User(3, "technician1", "Technician One", "Technician", "HQ", "IT"),
        User(4, "technician2", "Technician Two", "Technician", "DC1", "IT"),
        User(5, "technician3", "Technician Three", "Technician", "BR1", "IT"),
        User(6, "technician4", "Technician Four", "Technician", "HQ", "IT"),
        User(7, "manager1", "Manager One", "Manager", "HQ", "OPS"),
        User(8, "manager2", "Manager Two", "Manager", "BR1", "FIN"),
        User(9, "manager3", "Manager Three", "Manager", "HQ", "HR"),
        User(10, "manager4", "Manager Four", "Manager", "DC1", "IT"),
        User(11, "enduser1", "End User One", "EndUser", "HQ", "FIN"),
        User(12, "enduser2", "End User Two", "EndUser", "HQ", "HR"),
        User(13, "enduser3", "End User Three", "EndUser", "BR1", "OPS"),
        User(14, "enduser4", "End User Four", "EndUser", "DC1", "OPS"),
        User(15, "enduser5", "End User Five", "EndUser", "HQ", "FIN"),
        User(16, "enduser6", "End User Six", "EndUser", "BR1", "HR"),
        User(17, "enduser7", "End User Seven", "EndUser", "HQ", "OPS"),
        User(18, "enduser8", "End User Eight", "EndUser", "DC1", "FIN"),
        User(19, "enduser9", "End User Nine", "EndUser", "BR1", "OPS"),
        User(20, "enduser10", "End User Ten", "EndUser", "HQ", "HR"),
    ];

    public async Task<SeedResult> SeedAsync(CancellationToken cancellationToken = default)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        var existingSiteCodes = await dbContext.Sites.Select(site => site.Code).ToHashSetAsync(cancellationToken);
        var sitesToAdd = Sites.Where(site => !existingSiteCodes.Contains(site.Code)).ToArray();
        dbContext.Sites.AddRange(sitesToAdd);

        var existingDepartmentCodes = await dbContext.Departments.Select(department => department.Code)
            .ToHashSetAsync(cancellationToken);
        var departmentsToAdd = Departments.Where(department => !existingDepartmentCodes.Contains(department.Code)).ToArray();
        dbContext.Departments.AddRange(departmentsToAdd);
        await dbContext.SaveChangesAsync(cancellationToken);

        var siteIds = await dbContext.Sites.ToDictionaryAsync(site => site.Code, site => site.Id, cancellationToken);
        var departmentIds = await dbContext.Departments.ToDictionaryAsync(
            department => department.Code,
            department => department.Id,
            cancellationToken);
        var existingUsernames = await dbContext.UserProfiles.Select(user => user.Username)
            .ToHashSetAsync(cancellationToken);
        var usersToAdd = Users.Where(user => !existingUsernames.Contains(user.Username)).Select(user => new UserProfile
        {
            Id = user.Id,
            Username = user.Username,
            Email = $"{user.Username}@example.test",
            DisplayName = user.DisplayName,
            Role = user.Role,
            SiteId = siteIds[user.SiteCode],
            DepartmentId = departmentIds[user.DepartmentCode],
        }).ToArray();
        dbContext.UserProfiles.AddRange(usersToAdd);
        await dbContext.SaveChangesAsync(cancellationToken);

        // Derived from the people above rather than listed separately, so the department-to-location
        // map can never contradict where the seeded staff actually sit. This is also the evidence for
        // the many-to-many: IT alone spans HQ, DC1 and BR1.
        var existingLinks = await dbContext.DepartmentSites
            .Select(link => new { link.DepartmentId, link.SiteId }).ToListAsync(cancellationToken);
        var linksToAdd = Users
            .Select(user => new { DepartmentId = departmentIds[user.DepartmentCode], SiteId = siteIds[user.SiteCode] })
            .Distinct()
            .Where(pair => !existingLinks.Any(
                link => link.DepartmentId == pair.DepartmentId && link.SiteId == pair.SiteId))
            .Select(pair => new DepartmentSite { DepartmentId = pair.DepartmentId, SiteId = pair.SiteId })
            .ToArray();
        dbContext.DepartmentSites.AddRange(linksToAdd);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new SeedResult(sitesToAdd.Length, departmentsToAdd.Length, usersToAdd.Length);
    }

    private static UserSeed User(
        int sequence,
        string username,
        string displayName,
        string role,
        string siteCode,
        string departmentCode) =>
        new(Guid.Parse($"01980000-0000-7000-8000-00000000{sequence + 200:0000}"), username, displayName, role, siteCode, departmentCode);

    private sealed record UserSeed(
        Guid Id,
        string Username,
        string DisplayName,
        string Role,
        string SiteCode,
        string DepartmentCode);
}