using Microsoft.EntityFrameworkCore;

using Platform.Data;
using Platform.Seeding;

var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__database");
if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine("ConnectionStrings__database is required.");
    return 1;
}

var options = new DbContextOptionsBuilder<PlatformDbContext>()
    .UseNpgsql(connectionString)
    .Options;
await using var dbContext = new PlatformDbContext(options);
await dbContext.Database.MigrateAsync();

var result = await new DemoDataSeeder(dbContext).SeedAsync();
Console.WriteLine($"Demo data ready. Added {result.SitesAdded} sites, {result.DepartmentsAdded} departments, and {result.UsersAdded} users.");
return 0;