using System.Security.Claims;

using FluentValidation;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Modules.Monitoring.Data;
using Modules.Monitoring.Features.Devices;

namespace Modules.Monitoring.Features.Runbooks;

public static class RunbookEndpoints
{
    public static IEndpointRouteBuilder MapRunbookEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Three audiences, three policies, and the split is the security design of this package.
        //
        // Administering the allowlist — which allowlisted runbooks this estate registers, with what
        // bounds, and which alerts start them — is `AdminOnly`. It decides what may ever run
        // unattended, which is a different act from running one.
        //
        // Running one and reading the history is `CanRunRunbooks`, WP-5.6's own operator policy. It is
        // new rather than borrowed: WP-5.5 deliberately added no policy and put the rule in each
        // widget, and STATUS says plainly that a runbook is the opposite case — an execution path wants
        // a door of its own.
        //
        // The channel is `CanPoll`, the agent's own credential, which ARCHITECTURE §6 keeps disjoint
        // from every operator policy. An operator cannot claim an execution and an agent cannot request
        // one; neither can do the other's half, which is what stops the channel becoming a way to run
        // something by asking for it.
        var registry = endpoints.MapGroup("/api/runbooks").RequireAuthorization("AdminOnly");
        var operators = endpoints.MapGroup("/api/runbooks").RequireAuthorization("CanRunRunbooks");
        var agent = endpoints.MapGroup("/api/pollers").RequireAuthorization("CanPoll");

        // ---- the allowlist itself ----

        // What may ever be registered. Read-only by construction: it is compiled into the server, so
        // there is no POST beside it and no way to add to it over HTTP.
        operators.MapGet("/catalogue", (IRunbookRegistryService service) =>
            Results.Ok(service.Catalogue.Select(definition => new
            {
                definition.Key,
                definition.Name,
                definition.Description,
                definition.DefaultTimeoutSeconds,
                Parameters = definition.Parameters.Select(parameter => new RunbookParameterResponse(
                    parameter.Name,
                    parameter.Description,
                    parameter.IsRequired,
                    parameter.MaxLength,
                    parameter.Example)),
            })));

        operators.MapGet("/", async (IRunbookRegistryService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.ListAsync(cancellationToken)));

        operators.MapGet("/{key}", async (string key, IRunbookRegistryService service,
                CancellationToken cancellationToken) =>
            await service.GetAsync(key, cancellationToken) is { } runbook
                ? Results.Ok(runbook)
                : NotFound());

        // ---- registry administration ----

        registry.MapPost("/", async (CreateRunbookRequest request, ClaimsPrincipal user,
            IRunbookRegistryService service, CancellationToken cancellationToken) =>
        {
            var validation = await new CreateRunbookValidator().ValidateAsync(request, cancellationToken);
            if (!validation.IsValid) return Results.ValidationProblem(validation.ToDictionary());
            var result = await service.CreateAsync(request, user, cancellationToken);
            return result.Outcome switch
            {
                RunbookOutcome.Success =>
                    Results.Created($"/api/runbooks/{result.Runbook!.Key}", result.Runbook),
                _ => Problem(result.Outcome, result.Errors, result.Error, request.Key),
            };
        });

        registry.MapPut("/{key}", async (string key, UpdateRunbookRequest request, ClaimsPrincipal user,
            IRunbookRegistryService service, CancellationToken cancellationToken) =>
        {
            var validation = await new UpdateRunbookValidator().ValidateAsync(request, cancellationToken);
            if (!validation.IsValid) return Results.ValidationProblem(validation.ToDictionary());
            var result = await service.UpdateAsync(key, request, user, cancellationToken);
            return result.Outcome switch
            {
                RunbookOutcome.Success => Results.Ok(result.Runbook),
                _ => Problem(result.Outcome, result.Errors, result.Error, key),
            };
        });

        registry.MapDelete("/{key}", async (string key, ClaimsPrincipal user,
                IRunbookRegistryService service, CancellationToken cancellationToken) =>
            await service.DeleteAsync(key, user, cancellationToken) switch
            {
                RunbookOutcome.Success => Results.NoContent(),
                var outcome => Problem(outcome, null, null, key),
            });

        registry.MapPost("/{key}/triggers", async (string key, SaveRunbookTriggerRequest request,
            ClaimsPrincipal user, IRunbookRegistryService service, CancellationToken cancellationToken) =>
        {
            var validation = await new SaveTriggerValidator().ValidateAsync(request, cancellationToken);
            if (!validation.IsValid) return Results.ValidationProblem(validation.ToDictionary());
            var result = await service.AddTriggerAsync(key, request, user, cancellationToken);
            return result.Outcome switch
            {
                RunbookOutcome.Success =>
                    Results.Created($"/api/runbooks/{key}/triggers/{result.Trigger!.Id}", result.Trigger),
                _ => Problem(result.Outcome, result.Errors, result.Error, key),
            };
        });

        registry.MapPut("/{key}/triggers/{triggerId:guid}", async (string key, Guid triggerId,
            SaveRunbookTriggerRequest request, ClaimsPrincipal user, IRunbookRegistryService service,
            CancellationToken cancellationToken) =>
        {
            var validation = await new SaveTriggerValidator().ValidateAsync(request, cancellationToken);
            if (!validation.IsValid) return Results.ValidationProblem(validation.ToDictionary());
            var result = await service.UpdateTriggerAsync(key, triggerId, request, user, cancellationToken);
            return result.Outcome switch
            {
                RunbookOutcome.Success => Results.Ok(result.Trigger),
                _ => Problem(result.Outcome, result.Errors, result.Error, key),
            };
        });

        registry.MapDelete("/{key}/triggers/{triggerId:guid}", async (string key, Guid triggerId,
                ClaimsPrincipal user, IRunbookRegistryService service, CancellationToken cancellationToken) =>
            await service.DeleteTriggerAsync(key, triggerId, user, cancellationToken) switch
            {
                RunbookOutcome.Success => Results.NoContent(),
                var outcome => Problem(outcome, null, null, key),
            });

        // ---- executions ----

        // POST, because an execution is not CRUD — the CONVENTIONS rule that gave tickets
        // `/api/tickets/{id}/transitions`. Addressed by key rather than by id so that the refusal in the
        // WP's verification list is the one an operator can actually perform: POST a key nobody
        // allowlisted and the answer is 403, not a 404 about a Guid.
        operators.MapPost("/{key}/executions", async (string key, RunRunbookRequest request,
            ClaimsPrincipal user, IRunbookExecutionService service, CancellationToken cancellationToken) =>
        {
            var result = await service.RequestAsync(key, request, user, cancellationToken);
            return result.Outcome switch
            {
                RunbookOutcome.Success =>
                    Results.Created($"/api/runbook-executions/{result.Execution!.Id}", result.Execution),
                _ => Problem(result.Outcome, result.Errors, result.Error, key),
            };
        });

        var executions = endpoints.MapGroup("/api/runbook-executions")
            .RequireAuthorization("CanRunRunbooks");

        executions.MapGet("/", async (Guid? runbookId, Guid? deviceId, string? status, int? page,
            int? pageSize, IRunbookExecutionService service, CancellationToken cancellationToken) =>
        {
            RunbookExecutionStatus? parsed = null;
            if (!string.IsNullOrWhiteSpace(status))
            {
                // Spelt, not numbered — see the same guard on the result report. `?status=3` would
                // otherwise silently mean `Failed`.
                if (!Enum.TryParse<RunbookExecutionStatus>(status, ignoreCase: true, out var value)
                    || !Enum.IsDefined(value)
                    || !string.Equals(value.ToString(), status.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    return Results.ValidationProblem(RunbookMapping.Field(
                        nameof(status),
                        "Status must be Pending, Dispatched, Succeeded, Failed or TimedOut.")
                        .ToDictionary(entry => entry.Key, entry => entry.Value));
                }

                parsed = value;
            }

            return Results.Ok(await service.ListAsync(
                new RunbookExecutionListRequest(runbookId, deviceId, parsed, page ?? 1, pageSize ?? 25),
                cancellationToken));
        });

        executions.MapGet("/{id:guid}", async (Guid id, IRunbookExecutionService service,
                CancellationToken cancellationToken) =>
            await service.GetAsync(id, cancellationToken) is { } execution
                ? Results.Ok(execution)
                : Results.Problem(
                    statusCode: StatusCodes.Status404NotFound, title: "Runbook execution not found."));

        // ---- the agent channel ----

        agent.MapGet("/{name}/runbook-executions", async (string name, IRunbookDispatchService service,
            CancellationToken cancellationToken) =>
        {
            var result = await service.ClaimAsync(name, cancellationToken);
            return result.Outcome switch
            {
                MonitoringOutcome.Success => Results.Ok(result.Dispatch),
                MonitoringOutcome.NotFound => PollerNotFound(),
                var outcome => throw new InvalidOperationException($"Unknown monitoring outcome '{outcome}'."),
            };
        });

        agent.MapPost("/{name}/runbook-executions/{id:guid}/results", async (string name, Guid id,
            ReportRunbookResultRequest request, IRunbookDispatchService service,
            CancellationToken cancellationToken) =>
        {
            var result = await service.ReportAsync(name, id, request, cancellationToken);
            return result.Outcome switch
            {
                MonitoringOutcome.Success => Results.Ok(result.Execution),
                MonitoringOutcome.Invalid => Results.ValidationProblem(result.Errors!),
                MonitoringOutcome.NotFound => Results.Problem(
                    statusCode: StatusCodes.Status404NotFound,
                    title: "Runbook execution not found.",
                    detail: "It does not exist, or it is not one this poller holds."),
                MonitoringOutcome.Duplicate => Results.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    title: "Runbook execution is already finished.",
                    detail: result.Error),
                var outcome => throw new InvalidOperationException($"Unknown monitoring outcome '{outcome}'."),
            };
        });

        return endpoints;
    }

    /// <summary>
    /// One place every non-success outcome becomes a status code, so the registry and the execution
    /// path cannot disagree about what a refusal looks like.
    /// </summary>
    private static IResult Problem(
        RunbookOutcome outcome,
        IReadOnlyDictionary<string, string[]>? errors,
        string? error,
        string key) => outcome switch
    {
        RunbookOutcome.Invalid => Results.ValidationProblem(errors!),
        RunbookOutcome.NotFound => NotFound(),
        RunbookOutcome.Duplicate => Results.Problem(
            statusCode: StatusCodes.Status409Conflict, title: "Runbook conflict.", detail: error),
        // 403 rather than 404, and the wording is deliberate. This is the WP's own verification case:
        // asking to execute something nobody allowlisted is refused as forbidden, because the platform
        // knows perfectly well what was asked for and is declining — "not found" would suggest that
        // registering it would help.
        RunbookOutcome.NotAllowlisted => Results.Problem(
            statusCode: StatusCodes.Status403Forbidden,
            title: "Not an allowlisted runbook.",
            detail: $"'{key}' is not in this platform's runbook catalogue. Runbooks cannot be added over the API; "
                + "the catalogue is compiled into the server and there is no endpoint that accepts a script."),
        RunbookOutcome.Disabled => Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Runbook is disabled.",
            detail: error),
        RunbookOutcome.RateLimited => Results.Problem(
            statusCode: StatusCodes.Status429TooManyRequests,
            title: "Runbook rate limit reached.",
            detail: error),
        RunbookOutcome.AlreadyRequested => Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Runbook has already run for this alert.",
            detail: error),
        RunbookOutcome.InUse => Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Runbook has executions.",
            detail: "Disable it instead. Deleting it would take the record of everything it has run with it."),
        _ => throw new InvalidOperationException($"Unknown runbook outcome '{outcome}'."),
    };

    private static IResult NotFound() =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Runbook not found.");

    private static IResult PollerNotFound() => Results.Problem(
        statusCode: StatusCodes.Status404NotFound,
        title: "Poller not found.",
        detail: "Register the poller before fetching its work.");

    private sealed class CreateRunbookValidator : AbstractValidator<CreateRunbookRequest>
    {
        public CreateRunbookValidator()
        {
            RuleFor(request => request.Key).NotEmpty().MaximumLength(100);
            RuleFor(request => request.Name).MaximumLength(200);
            RuleFor(request => request.Description).MaximumLength(2_000);
        }
    }

    private sealed class UpdateRunbookValidator : AbstractValidator<UpdateRunbookRequest>
    {
        public UpdateRunbookValidator()
        {
            RuleFor(request => request.Name).MaximumLength(200);
            RuleFor(request => request.Description).MaximumLength(2_000);
        }
    }

    private sealed class SaveTriggerValidator : AbstractValidator<SaveRunbookTriggerRequest>
    {
        public SaveTriggerValidator()
        {
            RuleFor(request => request.MetricName).NotEmpty().MaximumLength(100);
            RuleFor(request => request.MinimumSeverity).NotEmpty().MaximumLength(20);
        }
    }
}
