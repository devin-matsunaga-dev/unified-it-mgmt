using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Platform.Data;
using Platform.Directory;
using Platform.Seeding;
using Platform.Vault;
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

// The credential vault (WP-3.11), before the monitored devices that name its rows.
//
// Data Protection has to be configured exactly as the API configures it or the ciphertext this writes
// is one the API cannot read: the same application name and the same key ring, which lives in
// `platform.data_protection_keys` rather than on either process's filesystem. That is the whole reason
// this seeder builds a container instead of newing up a protector — a default provider here would mint
// its own key, encrypt the communities with it, throw the key away when the process exits, and leave
// every seeded SNMP check permanently unauthenticatable.
var vaultServices = new ServiceCollection();
vaultServices.AddDbContext<PlatformDbContext>(options => options.UseNpgsql(connectionString));
vaultServices.AddSingleton<DataProtectionKeyRepository>();
vaultServices.AddDataProtection()
    .SetApplicationName("it-platform")
    .Services
    .AddSingleton<IConfigureOptions<KeyManagementOptions>>(provider =>
        new ConfigureNamedOptions<KeyManagementOptions>(Options.DefaultName, keyManagement =>
            keyManagement.XmlRepository = provider.GetRequiredService<DataProtectionKeyRepository>()));
vaultServices.AddScoped<ICredentialProtector, CredentialProtector>();
await using var vaultProvider = vaultServices.BuildServiceProvider();
await using var vaultScope = vaultProvider.CreateAsyncScope();
var credentialResult = await new CredentialSeeder(
    dbContext, vaultScope.ServiceProvider.GetRequiredService<ICredentialProtector>()).SeedAsync(
        MonitoringDemoSeeder.HealthyCommunity, MonitoringDemoSeeder.DegradedCommunity);
Console.WriteLine($"Credential vault ready. Added {credentialResult.CredentialsAdded} credentials.");

// Notification routing (WP-3.10). The chat channel is seeded only when a webhook URL is supplied:
// a placeholder would fail every Critical alert and read as a broken feature.
var webhookUrl = Environment.GetEnvironmentVariable("Notifications__Seed__WebhookUrl");
var webhookKind = Enum.TryParse<NotificationChannelKind>(
    Environment.GetEnvironmentVariable("Notifications__Seed__WebhookKind"), ignoreCase: true, out var parsedKind)
    ? parsedKind
    : NotificationChannelKind.Teams;
var notificationResult = await new NotificationRoutingSeeder(dbContext).SeedAsync(
    Environment.GetEnvironmentVariable("Notifications__Seed__OperationsEmail")
        ?? "it-operations@it-platform.local",
    webhookUrl,
    webhookKind);
Console.WriteLine($"Notification routing ready. Added {notificationResult.ChannelsAdded} channels and {notificationResult.RulesAdded} routing rules"
    + (notificationResult.WebhookSeeded ? $" (chat channel seeded as {webhookKind})." : "; no webhook URL was supplied, so no chat channel was seeded."));
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
        // WP-3.12 appends two more, both named rather than positional for the reason above: the
        // down-able switch (its own simulator container, the one the Phase 3 demo stops) and the
        // customer portal (the mock HTTP target). Appended after the existing four so that nothing
        // already seeded changes device — the ids of the first four are fixed and their CIs are what
        // WP-3.7 gave owners to.
        [
            .. assetsResult.NetworkCiIds.Take(3),
            .. (assetsResult.CiIds.TryGetValue("svc-mail", out var mailCiId)
                ? [mailCiId]
                : assetsResult.ServiceCiIds.Take(1)),
            .. (assetsResult.CiIds.TryGetValue("dc1-acc-sw-01", out var downableCiId)
                ? [downableCiId]
                : assetsResult.NetworkCiIds.Skip(3).Take(1)),
            .. (assetsResult.CiIds.TryGetValue("svc-portal", out var portalCiId)
                ? [portalCiId]
                : assetsResult.ServiceCiIds.Skip(1).Take(1)),
        ],
        Environment.GetEnvironmentVariable("Monitoring__Seed__PollerGroup") ?? "default",
        Environment.GetEnvironmentVariable("Monitoring__Seed__ServiceAddress") ?? "mailhog",
        int.TryParse(
            Environment.GetEnvironmentVariable("Monitoring__Seed__ServiceTcpPort"), out var serviceTcpPort)
            ? serviceTcpPort
            : 1025,
        Environment.GetEnvironmentVariable("Monitoring__Seed__ServiceHttpUrl")
            ?? "http://mailhog:8025/",
        // Null rather than Guid.Empty when a key is missing: a check pointed at the empty GUID would
        // be refused by the vault every cycle instead of falling back to its plaintext parameter.
        credentialResult.CredentialIds.TryGetValue(CredentialSeeder.HealthyKey, out var healthyCredentialId)
            ? healthyCredentialId
            : null,
        credentialResult.CredentialIds.TryGetValue(CredentialSeeder.DegradedKey, out var degradedCredentialId)
            ? degradedCredentialId
            : null,
        Environment.GetEnvironmentVariable("Monitoring__Seed__DownableSnmpAddress") ?? "snmpsim-downable",
        Environment.GetEnvironmentVariable("Monitoring__Seed__HttpTargetAddress") ?? "http-target",
        int.TryParse(
            Environment.GetEnvironmentVariable("Monitoring__Seed__HttpTargetPort"), out var httpTargetPort)
            ? httpTargetPort
            : 80));
Console.WriteLine($"Monitored devices ready. Added {monitoringResult.DevicesAdded} devices and {monitoringResult.ChecksAdded} checks.");
Console.WriteLine($"Scan profiles ready. Added {monitoringResult.ScanProfilesAdded} profiles.");
return 0;
