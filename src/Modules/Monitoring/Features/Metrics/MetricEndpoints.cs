using FluentValidation;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Modules.Monitoring.Features.Metrics;

/// <summary>
/// The read side of metrics storage: what a device reports, one metric's series over a range, and
/// the text facts it has told us about itself. WP-3.9's charts are the intended caller; nothing in
/// the SPA reads these yet.
/// </summary>
public static class MetricEndpoints
{
    /// <summary>Range a series request defaults to when the caller names neither end.</summary>
    private static readonly TimeSpan DefaultRange = TimeSpan.FromHours(6);

    public static IEndpointRouteBuilder MapMetricEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var devices = endpoints.MapGroup("/api/monitored-devices").RequireAuthorization("CanManageMonitoring");

        devices.MapGet("/{id:guid}/metrics", async (Guid id, IMetricQueryService service,
                CancellationToken cancellationToken) =>
            await service.ListMetricsAsync(id, cancellationToken) is { } metrics
                ? Results.Ok(metrics)
                : NotFound("Monitored device not found."));

        devices.MapGet("/{id:guid}/metrics/series", async (
            Guid id,
            string? metric,
            DateTimeOffset? from,
            DateTimeOffset? to,
            MetricResolution? resolution,
            MetricAggregation? aggregation,
            Guid? checkId,
            IMetricQueryService service,
            CancellationToken cancellationToken) =>
        {
            // The window is anchored to 'to' rather than to now, so asking for a fixed 'from' with no
            // 'to' reads forward from it instead of silently becoming a different range.
            var end = to ?? DateTimeOffset.UtcNow;
            var request = new MetricSeriesRequest(
                id,
                metric ?? string.Empty,
                from ?? end - DefaultRange,
                end,
                resolution ?? MetricResolution.Auto,
                aggregation ?? MetricAggregation.Avg,
                checkId);

            var validation = await new MetricSeriesValidator().ValidateAsync(request, cancellationToken);
            if (!validation.IsValid) return Results.ValidationProblem(validation.ToDictionary());

            var result = await service.GetSeriesAsync(request, cancellationToken);
            return result.Outcome switch
            {
                MetricQueryOutcome.Success => Results.Ok(result.Series),
                MetricQueryOutcome.NotFound => NotFound("Monitored device not found."),
                MetricQueryOutcome.Invalid => Results.ValidationProblem(
                    new Dictionary<string, string[]> { [result.ErrorKey ?? "range"] = [result.Error!] }),
                var outcome => throw new InvalidOperationException($"Unknown metric query outcome '{outcome}'."),
            };
        });

        devices.MapGet("/{id:guid}/inventory", async (Guid id, IMetricQueryService service,
                CancellationToken cancellationToken) =>
            await service.GetInventoryAsync(id, cancellationToken) is { } inventory
                ? Results.Ok(inventory)
                : NotFound("Monitored device not found."));

        return endpoints;
    }

    private static IResult NotFound(string title) =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: title);

    private sealed class MetricSeriesValidator : AbstractValidator<MetricSeriesRequest>
    {
        public MetricSeriesValidator()
        {
            RuleFor(request => request.Metric).NotEmpty().MaximumLength(100);
            RuleFor(request => request.Resolution).IsInEnum();
            RuleFor(request => request.Aggregation).IsInEnum();
        }
    }
}
