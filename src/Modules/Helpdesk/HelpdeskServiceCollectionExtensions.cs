using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Modules.Helpdesk.Data;
using Modules.Helpdesk.Features.AlertTickets;
using Modules.Helpdesk.Features.Tickets;
using Modules.Helpdesk.Features.Assignments;
using Modules.Helpdesk.Features.CannedResponses;
using Modules.Helpdesk.Features.Categories;
using Modules.Helpdesk.Features.Dashboards;
using Modules.Helpdesk.Features.Views;
using Modules.Helpdesk.Features.Interactions;
using Modules.Helpdesk.Features.Problems;
using Modules.Helpdesk.Features.Sla;
using Modules.Helpdesk.Features.Email;
using Modules.Helpdesk.Features.TicketCis;
using Modules.Helpdesk.Features.Search;
using Platform.Dashboards;
using Platform.Integration;
using Platform.Search;
using Quartz;

namespace Modules.Helpdesk;

public static class HelpdeskServiceCollectionExtensions
{
    public static IServiceCollection AddHelpdeskServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddDbContext<HelpdeskDbContext>((provider, options) =>
        {
            var connectionString = provider.GetRequiredService<IConfiguration>().GetConnectionString("database")
                ?? throw new InvalidOperationException("Connection string 'database' is required.");
            options.UseNpgsql(connectionString);
        });
        services.AddScoped<ITicketService, TicketService>();
        // WP-5.4. Registered here rather than in Platform because the query is over Helpdesk's own
        // schema; the service that merges the five holds no reference to any module.
        services.AddScoped<ISearchSource, TicketSearchSource>();
        // WP-5.5. Two widgets over Helpdesk's own schema; the dashboard that shows them beside four
        // other modules' numbers references none of them.
        services.AddScoped<IDashboardWidget, SlaHealthWidget>();
        services.AddScoped<IDashboardWidget, OpenByPriorityWidget>();
        services.AddScoped<ICategoryService, CategoryService>();
        services.AddScoped<IAssignmentService, AssignmentService>();
        services.AddScoped<ITicketCiLinkService, TicketCiLinkService>();
        services.AddScoped<ITicketLinkDirectory, TicketCiLinkDirectory>();
        services.AddScoped<ITicketImpactDirectory, TicketImpactDirectory>();
        services.AddScoped<ICiTicketHistoryDirectory, CiTicketHistoryDirectory>();
        services.AddScoped<ITicketViewService, TicketViewService>();
        services.AddScoped<ICannedResponseService, CannedResponseService>();
        services.AddScoped<IInteractionService, InteractionService>();
        services.AddScoped<ISlaService, SlaService>();
        // WP-5.7. Registered concretely as well as behind its interface because the suggestion service
        // reuses its mapping, its subject lookup and its "which of these incidents are still free"
        // read — one definition of what a problem looks like on the wire, rather than two that drift.
        services.AddScoped<ProblemService>();
        services.AddScoped<IProblemService>(provider => provider.GetRequiredService<ProblemService>());
        services.AddScoped<IProblemSuggestionService, ProblemSuggestionService>();
        services.AddOptions<ProblemDetectionOptions>()
            .Bind(configuration.GetSection(ProblemDetectionOptions.SectionName))
            .Validate(options => options.MinimumIncidents >= 2,
                $"{ProblemDetectionOptions.SectionName}:MinimumIncidents must be at least 2 — one incident is not a recurrence.")
            .Validate(options => options.WindowDays >= 1,
                $"{ProblemDetectionOptions.SectionName}:WindowDays must be at least 1.")
            .Validate(options => options.DismissalCooldownDays >= 0,
                $"{ProblemDetectionOptions.SectionName}:DismissalCooldownDays cannot be negative.")
            .Validate(options => options.MaxSuggestionsPerRun >= 1,
                $"{ProblemDetectionOptions.SectionName}:MaxSuggestionsPerRun must be at least 1 — a pass that may raise nothing is the same as Enabled: false.")
            .ValidateOnStart();
        services.AddScoped<IEmailIngestionService, EmailIngestionService>();
        services.AddScoped<IEmailMailbox, MailKitEmailMailbox>();
        services.AddScoped<IAlertAutomationGuard, RedisAlertAutomationGuard>();
        services.AddScoped<IAlertTicketAutomation, AlertTicketAutomation>();

        services.AddOptions<AlertTicketOptions>()
            .Bind(configuration.GetSection(AlertTicketOptions.SectionName))
            .Validate(options => options.RateLimitPerRulePerMinute >= 1,
                $"{AlertTicketOptions.SectionName}:RateLimitPerRulePerMinute must be at least 1.")
            .Validate(options => options.BreakerThreshold >= 1,
                $"{AlertTicketOptions.SectionName}:BreakerThreshold must be at least 1.")
            .Validate(options => options.BreakerWindowSeconds >= 1,
                $"{AlertTicketOptions.SectionName}:BreakerWindowSeconds must be at least 1.")
            .Validate(options => options.BreakerCooldownSeconds >= 1,
                $"{AlertTicketOptions.SectionName}:BreakerCooldownSeconds must be at least 1.")
            .Validate(options => !string.IsNullOrWhiteSpace(options.AdminRecipient),
                $"{AlertTicketOptions.SectionName}:AdminRecipient is required — a breaker nobody hears about is not a breaker.")
            .ValidateOnStart();
        services.AddSingleton<IAttachmentStorage, MinioAttachmentStorage>();
        services.AddSingleton<IAntivirusScanner, NoOpAntivirusScanner>();
        services.AddQuartz(quartz =>
        {
            var jobKey = new JobKey("sla-evaluation");
            quartz.AddJob<SlaEvaluationJob>(builder => builder.WithIdentity(jobKey));
            quartz.AddTrigger(builder => builder.ForJob(jobKey).WithIdentity("sla-evaluation-every-minute")
                .StartNow().WithSimpleSchedule(schedule => schedule.WithIntervalInMinutes(1).RepeatForever()));
            var emailJobKey = new JobKey("email-ingestion");
            quartz.AddJob<EmailIngestionJob>(builder => builder.WithIdentity(emailJobKey));
            quartz.AddTrigger(builder => builder.ForJob(emailJobKey).WithIdentity("email-ingestion-every-minute")
                .StartNow().WithSimpleSchedule(schedule => schedule.WithIntervalInMinutes(1).RepeatForever()));

            // WP-5.7. Daily from host start-up rather than at a fixed hour, matching the contract expiry
            // and licence compliance passes: the pass is idempotent — a second run finds its own
            // suggestions open and raises nothing — so an extra run costs one query and a recurrence is
            // visible without waiting for the small hours.
            var problemDetectionKey = new JobKey("problem-detection");
            quartz.AddJob<ProblemDetectionJob>(builder => builder.WithIdentity(problemDetectionKey));
            quartz.AddTrigger(builder => builder.ForJob(problemDetectionKey).WithIdentity("problem-detection-daily")
                .StartNow().WithSimpleSchedule(schedule => schedule.WithIntervalInHours(24).RepeatForever()));
        });

        return services;
    }

    /// <summary>
    /// The consumers this module owns, handed to Platform's single bus registration the same way
    /// <c>AddMonitoringConsumers</c> is — MassTransit is configured once per host, but a consumer
    /// belongs to the module that reacts.
    /// </summary>
    public static void AddHelpdeskConsumers(IBusRegistrationConfigurator bus)
    {
        ArgumentNullException.ThrowIfNull(bus);
        bus.AddConsumer<AlertRaisedConsumer>();
        bus.AddConsumer<AlertClearedConsumer>();
        // WP-5.6: the result of a remediation Monitoring ran, landing on the ticket for its alert.
        bus.AddConsumer<RunbookExecutionCompletedConsumer>();
    }
}
