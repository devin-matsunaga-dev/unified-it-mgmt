using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Modules.Helpdesk.Data;
using Modules.Helpdesk.Features.Tickets;
using Modules.Helpdesk.Features.Assignments;
using Modules.Helpdesk.Features.Interactions;
using Modules.Helpdesk.Features.Sla;
using Modules.Helpdesk.Features.Email;
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
        services.AddScoped<IAssignmentService, AssignmentService>();
        services.AddScoped<IInteractionService, InteractionService>();
        services.AddScoped<ISlaService, SlaService>();
        services.AddScoped<IEmailIngestionService, EmailIngestionService>();
        services.AddScoped<IEmailMailbox, MailKitEmailMailbox>();
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

}
