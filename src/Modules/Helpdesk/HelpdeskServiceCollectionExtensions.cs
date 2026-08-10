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
using Modules.Helpdesk.Features.Views;
using Modules.Helpdesk.Features.Interactions;
using Modules.Helpdesk.Features.Sla;
using Modules.Helpdesk.Features.Email;
using Modules.Helpdesk.Features.TicketCis;
using Platform.Integration;
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
        services.AddScoped<ICategoryService, CategoryService>();
        services.AddScoped<IAssignmentService, AssignmentService>();
        services.AddScoped<ITicketCiLinkService, TicketCiLinkService>();
        services.AddScoped<ITicketLinkDirectory, TicketCiLinkDirectory>();
        services.AddScoped<ITicketViewService, TicketViewService>();
        services.AddScoped<ICannedResponseService, CannedResponseService>();
        services.AddScoped<IInteractionService, InteractionService>();
        services.AddScoped<ISlaService, SlaService>();
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
    }
}
