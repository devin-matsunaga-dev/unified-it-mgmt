using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Modules.Helpdesk.Data;
using Modules.Helpdesk.Features.Tickets;
using Modules.Helpdesk.Features.Assignments;

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

        return services;
    }

}
