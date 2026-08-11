using Microsoft.EntityFrameworkCore;

using Platform.Data;
using Platform.Directory;
using Platform.Seeding;
using Modules.Assets.Data;
using Modules.Assets.Seeding;
using Modules.Helpdesk.Data;
using Modules.Helpdesk.Seeding;
using Modules.Monitoring.Data;
using Modules.Monitoring.Seeding;

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
Console.WriteLine($"Helpdesk demo data ready. Added {helpdeskResult.TeamsAdded} teams, {helpdeskResult.QueuesAdded} queues, {helpdeskResult.MembersAdded} team members, {helpdeskResult.CategoriesAdded} categories, {helpdeskResult.CustomFieldsAdded} custom fields, {helpdeskResult.CannedResponsesAdded} canned responses, and {helpdeskResult.ViewsAdded} shared views.");
var historyResult = await new HelpdeskHistorySeeder(helpdeskDbContext).SeedAsync();
Console.WriteLine($"Helpdesk history ready. Added {historyResult.CalendarsAdded} business hours calendars, {historyResult.PoliciesAdded} SLA policies, {historyResult.TicketsAdded} tickets, {historyResult.TransitionsAdded} status transitions, {historyResult.CommentsAdded} comments, {historyResult.WorklogsAdded} worklogs, and {historyResult.SlasAdded} ticket SLAs.");

// The estate is seeded last: it resolves owners through the platform directory, and its ticket links
// are attached to the backlog the history seeder has just written.
var assetsOptions = new DbContextOptionsBuilder<AssetsDbContext>()
    .UseNpgsql(connectionString)
    .Options;
await using var assetsDbContext = new AssetsDbContext(assetsOptions);
await assetsDbContext.Database.MigrateAsync();
var assetsResult = await new AssetsInfrastructureSeeder(assetsDbContext, new DirectoryService(dbContext)).SeedAsync();
Console.WriteLine($"Asset estate ready. Added {assetsResult.VendorsAdded} vendors, {assetsResult.ContractsAdded} contracts, {assetsResult.CustomFieldsAdded} CI custom fields, {assetsResult.CisAdded} configuration items, {assetsResult.RelationshipsAdded} relationships, {assetsResult.LifecycleEntriesAdded} lifecycle history entries, {assetsResult.AssignmentEntriesAdded} assignment log entries, and {assetsResult.CustomFieldValuesAdded} custom field values.");

var linkResult = await new HelpdeskCiLinkSeeder(helpdeskDbContext).SeedAsync(new CiLinkPlan(
    assetsResult.HardwareCiIds,
    assetsResult.NetworkCiIds,
    assetsResult.ServiceCiIds));
Console.WriteLine($"Ticket asset links ready. Added {linkResult.LinksAdded} ticket to CI links.");

// Monitored devices last: they name CIs the estate has just written, and Monitoring may not
// reference Assets, so the ids arrive as an argument the same way the ticket links' did.
var monitoringOptions = new DbContextOptionsBuilder<MonitoringDbContext>()
    .UseNpgsql(connectionString)
    .Options;
await using var monitoringDbContext = new MonitoringDbContext(monitoringOptions);
await monitoringDbContext.Database.MigrateAsync();
var monitoringResult = await new MonitoringDemoSeeder(monitoringDbContext).SeedAsync(
    new MonitoringSeedPlan(
        // Defaults keep `dotnet run --project src/Seeder` working outside Aspire; under `aspire run`
        // these name the simulator container as the poller's own container reaches it — by name on
        // the session network, which is the only route that works from inside a container.
        Environment.GetEnvironmentVariable("Monitoring__Seed__SnmpAddress") ?? "snmpsim",
        int.TryParse(Environment.GetEnvironmentVariable("Monitoring__Seed__SnmpPort"), out var snmpPort)
            ? snmpPort
            : 161,
        // Three network devices and then a service CI: WP-3.8's TCP and HTTP checks are about a
        // listener rather than a box, and the fourth device is the one they hang off. The first three
        // stay first — WP-3.7 gave exactly those an owner so an alert has context to show.
        // The service CI is named rather than taken by position, because the device polls the dev
        // mail container and "Corporate Email Service" is the CI that is actually true of it — the
        // first entry in ServiceCiIds is the finance reporting service, which made every screen in
        // the demo say a mail outage was a reporting outage. Falls back to the old positional pick so
        // a rename in the estate costs the apt name rather than the whole device.
        [
            .. assetsResult.NetworkCiIds.Take(3),
            .. (assetsResult.CiIds.TryGetValue("svc-mail", out var mailCiId)
                ? [mailCiId]
                : assetsResult.ServiceCiIds.Take(1)),
        ],
        Environment.GetEnvironmentVariable("Monitoring__Seed__PollerGroup") ?? "default",
        Environment.GetEnvironmentVariable("Monitoring__Seed__ServiceAddress") ?? "mailhog",
        int.TryParse(
            Environment.GetEnvironmentVariable("Monitoring__Seed__ServiceTcpPort"), out var serviceTcpPort)
            ? serviceTcpPort
            : 1025,
        Environment.GetEnvironmentVariable("Monitoring__Seed__ServiceHttpUrl")
            ?? "http://mailhog:8025/"));
Console.WriteLine($"Monitored devices ready. Added {monitoringResult.DevicesAdded} devices and {monitoringResult.ChecksAdded} checks.");
return 0;
