using Microsoft.EntityFrameworkCore;

using Platform.Data;
using Platform.Seeding;
using Modules.Helpdesk.Data;
using Modules.Helpdesk.Seeding;

var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__database");
if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine("ConnectionStrings__database is required.");
    return 1;
}

var platformOptions = new DbContextOptionsBuilder<PlatformDbContext>()
    .UseNpgsql(connectionString)
    .Options;
await using var dbContext = new PlatformDbContext(platformOptions);
await dbContext.Database.MigrateAsync();

var result = await new DemoDataSeeder(dbContext).SeedAsync();
Console.WriteLine($"Demo data ready. Added {result.SitesAdded} sites, {result.DepartmentsAdded} departments, and {result.UsersAdded} users.");
var helpdeskOptions = new DbContextOptionsBuilder<HelpdeskDbContext>()
    .UseNpgsql(connectionString)
    .Options;
await using var helpdeskDbContext = new HelpdeskDbContext(helpdeskOptions);
await helpdeskDbContext.Database.MigrateAsync();
var helpdeskResult = await new HelpdeskDemoDataSeeder(helpdeskDbContext).SeedAsync();
Console.WriteLine($"Helpdesk demo data ready. Added {helpdeskResult.TeamsAdded} teams, {helpdeskResult.QueuesAdded} queues, {helpdeskResult.MembersAdded} team members, {helpdeskResult.CategoriesAdded} categories, and {helpdeskResult.CustomFieldsAdded} custom fields.");
return 0;
