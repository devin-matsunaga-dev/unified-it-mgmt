using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;

using Contracts.Events;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Modules.Assets.Data;
using Modules.Monitoring.Data;
using Modules.Monitoring.Features.Metrics;

using Platform.Data;

namespace Infrastructure.Tests;

/// <summary>
/// The WP's verification chain against a real TimescaleDB: telemetry becomes rows, the query API
/// returns a series over them, and a retention policy shortened to an hour drops old-dated readings.
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class MetricsStorageIntegrationTests : IAsyncLifetime
{
    private readonly MetricsApplication _application;
    private HttpClient? _client;

    public MetricsStorageIntegrationTests(InfrastructureFixture infrastructure)
    {
        _application = new MetricsApplication(
            infrastructure.PostgresConnectionString,
            infrastructure.RabbitMqConnectionString,
            infrastructure.MinioConnectionString);
    }

    public async Task InitializeAsync()
    {
        _client = _application.CreateClient();
        await using var scope = _application.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<PlatformDbContext>().Database.MigrateAsync();
        await scope.ServiceProvider.GetRequiredService<AssetsDbContext>().Database.MigrateAsync();
        await scope.ServiceProvider.GetRequiredService<MonitoringDbContext>().Database.MigrateAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ---- the schema the migration is supposed to have built ----

    /// <summary>
    /// Without this the table is an ordinary relation that happens to have a time column: no chunks,
    /// no retention, no continuous aggregate. Everything else in this file would still pass.
    /// </summary>
    [Fact]
    public async Task DeviceMetrics_IsAHypertableWithOneDayChunks()
    {
        Assert.Equal(1, await ScalarAsync<long>(
            """
            SELECT count(*) FROM timescaledb_information.hypertables
            WHERE hypertable_schema = 'monitoring' AND hypertable_name = 'device_metrics'
            """));

        Assert.Equal(TimeSpan.FromDays(1), await ScalarAsync<TimeSpan>(
            """
            SELECT time_interval FROM timescaledb_information.dimensions
            WHERE hypertable_schema = 'monitoring' AND hypertable_name = 'device_metrics'
            """));
    }

    /// <summary>
    /// The WP-3.4 migration is hand-edited — EF generated the two tables and the Timescale DDL was
    /// written into it afterwards. This is what catches an entity change that never made it into a
    /// migration at all.
    /// </summary>
    [Fact]
    public async Task MonitoringMigrations_CurrentModel_HasNoPendingChanges()
    {
        await using var scope = _application.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<MonitoringDbContext>();

        Assert.False(context.Database.HasPendingModelChanges());
    }

    /// <summary>Raw 30 days and rolled-up 1 year, the WP's numbers, as real Timescale jobs.</summary>
    [Fact]
    public async Task RetentionPolicies_AreInstalledForBothResolutions()
    {
        Assert.Equal("30 days", await ScalarAsync<string>(
            """
            SELECT config ->> 'drop_after' FROM timescaledb_information.jobs
            WHERE proc_name = 'policy_retention' AND hypertable_name = 'device_metrics'
            """));

        Assert.Equal("365 days", await ScalarAsync<string>(
            """
            SELECT config ->> 'drop_after' FROM timescaledb_information.jobs
            WHERE proc_name = 'policy_retention' AND hypertable_name = 'device_metrics_5m'
            """));

        Assert.Equal(1, await ScalarAsync<long>(
            """
            SELECT count(*) FROM timescaledb_information.jobs
            WHERE proc_name = 'policy_refresh_continuous_aggregate' AND hypertable_name = 'device_metrics_5m'
            """));
    }

    // ---- ingestion ----

    [Fact]
    public async Task Telemetry_Ingested_BecomesASeriesTheQueryApiReturns()
    {
        var device = await CreateDeviceAsync();
        var checkId = Guid.CreateVersion7();
        var start = DateTimeOffset.UtcNow.AddMinutes(-10);

        await IngestAsync(Batch(device, checkId, start, [10d, 20d, 30d]));

        var from = start.AddMinutes(-1);
        var to = start.AddMinutes(10);
        var series = await GetAsync<MetricSeriesDto>(
            $"/api/monitored-devices/{device.Id}/metrics/series"
            + $"?metric=cpu.utilisation&from={Iso(from)}&to={Iso(to)}");

        Assert.Equal("Raw", series.Resolution);
        Assert.Equal("%", series.Unit);
        Assert.Equal([10d, 20d, 30d], series.Points.Select(point => point.Value));
        Assert.Equal(
            series.Points.Select(point => point.Timestamp).Order(),
            series.Points.Select(point => point.Timestamp));
        Assert.All(series.Points, point => Assert.Equal(1, point.SampleCount));
    }

    /// <summary>
    /// The two facts ingestion derives from the check result itself, which no poller sends: a failed
    /// check still produces a reading, so an unreachable device is not silence.
    /// </summary>
    [Fact]
    public async Task Telemetry_ForAFailedCheck_StoresAZeroSuccessReading()
    {
        var device = await CreateDeviceAsync();
        var checkId = Guid.CreateVersion7();
        var observed = DateTimeOffset.UtcNow.AddMinutes(-2);

        await IngestAsync(new DeviceTelemetryReported(
            Guid.CreateVersion7(), observed, "poller-1", "default", 1,
            [
                new DeviceCheckResult(device.Id, device.CiId, checkId, "Icmp", "Reachability",
                    device.Address, observed, Succeeded: false, LatencyMs: null,
                    Error: "Timed out after 5s", Metrics: []),
            ]));

        var series = await GetAsync<MetricSeriesDto>(
            $"/api/monitored-devices/{device.Id}/metrics/series"
            + $"?metric=check.success&from={Iso(observed.AddMinutes(-1))}&to={Iso(observed.AddMinutes(1))}");

        var point = Assert.Single(series.Points);
        Assert.Equal(0d, point.Value);
    }

    [Fact]
    public async Task Telemetry_WithATextSample_LandsInInventoryRatherThanTheHypertable()
    {
        var device = await CreateDeviceAsync();
        var checkId = Guid.CreateVersion7();
        var observed = DateTimeOffset.UtcNow.AddMinutes(-1);

        await IngestAsync(new DeviceTelemetryReported(
            Guid.CreateVersion7(), observed, "poller-1", "default", 1,
            [
                new DeviceCheckResult(device.Id, device.CiId, checkId, "Snmp", "System info",
                    device.Address, observed, true, 2.5, null,
                    [
                        new MetricSample("sysName", null, "core-sw-01", null),
                        new MetricSample("uptime.seconds", 86_400, null, "s"),
                    ]),
            ]));

        var inventory = await GetAsync<InventoryDto>($"/api/monitored-devices/{device.Id}/inventory");
        var fact = Assert.Single(inventory.Facts);
        Assert.Equal("sysName", fact.Name);
        Assert.Equal("core-sw-01", fact.Value);

        var metrics = await GetAsync<List<MetricSummaryDto>>($"/api/monitored-devices/{device.Id}/metrics");
        Assert.Equal(
            ["check.latency_ms", "check.success", "uptime.seconds"],
            metrics.Select(metric => metric.Metric).Order());
        Assert.DoesNotContain(metrics, metric => metric.Metric == "sysName");
        Assert.Equal(86_400d, metrics.Single(metric => metric.Metric == "uptime.seconds").LastValue);
    }

    /// <summary>
    /// The natural key doing its job. This is the backstop under the dedupe helper, so it is asserted
    /// against the storage directly rather than through the consumer.
    /// </summary>
    [Fact]
    public async Task Telemetry_IngestedTwice_StoresOneCopy()
    {
        var device = await CreateDeviceAsync();
        var checkId = Guid.CreateVersion7();
        var start = DateTimeOffset.UtcNow.AddMinutes(-5);
        var batch = Batch(device, checkId, start, [42d, 43d]);

        await IngestAsync(batch);
        await IngestAsync(batch);

        await using var scope = _application.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<MonitoringDbContext>();
        var stored = await context.DeviceMetrics
            .Where(metric => metric.DeviceId == device.Id && metric.MetricName == "cpu.utilisation")
            .CountAsync();
        Assert.Equal(2, stored);
    }

    // ---- one metric name, several checks ----

    /// <summary>
    /// Found by hand-verifying WP-3.4 against the live seeded estate: every check contributes
    /// `check.success` and `check.latency_ms`, so on the four-check seeded router those names are
    /// four series each. Asking for one without naming a check used to interleave them — an ICMP
    /// reading of 0.03 ms next to an SNMP one of 522 ms, plotted as a single line.
    /// </summary>
    [Fact]
    public async Task Series_ForAMetricSeveralChecksReport_IsRefusedUntilOneIsNamed()
    {
        var device = await CreateDeviceAsync();
        var icmp = Guid.CreateVersion7();
        var snmp = Guid.CreateVersion7();
        var start = DateTimeOffset.UtcNow.AddMinutes(-6);

        await IngestAsync(BatchForCheck(device, icmp, "Reachability", start, latencyMs: 0.05));
        await IngestAsync(BatchForCheck(device, snmp, "CPU", start, latencyMs: 522.7));

        using var ambiguous = Authenticated(
            HttpMethod.Get,
            $"/api/monitored-devices/{device.Id}/metrics/series?metric=check.latency_ms");
        using var response = await _client!.SendAsync(ambiguous);
        var problem = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("reported by 2 checks", problem, StringComparison.Ordinal);
        Assert.Contains("checkId", problem, StringComparison.Ordinal);

        // Naming one returns only that check's readings, at that check's own scale.
        var series = await GetAsync<MetricSeriesDto>(
            $"/api/monitored-devices/{device.Id}/metrics/series?metric=check.latency_ms&checkId={snmp}");
        Assert.Equal(snmp, series.CheckId);
        var point = Assert.Single(series.Points);
        Assert.Equal(522.7, point.Value);
    }

    /// <summary>The ordinary case must not have to name anything: one check, no `checkId` needed.</summary>
    [Fact]
    public async Task Series_ForAMetricOnlyOneCheckReports_NeedsNoCheckId()
    {
        var device = await CreateDeviceAsync();
        var checkId = Guid.CreateVersion7();
        await IngestAsync(Batch(device, checkId, DateTimeOffset.UtcNow.AddMinutes(-4), [7d]));

        var series = await GetAsync<MetricSeriesDto>(
            $"/api/monitored-devices/{device.Id}/metrics/series?metric=cpu.utilisation");

        Assert.Equal(checkId, series.CheckId);
        Assert.Equal(7d, Assert.Single(series.Points).Value);
    }

    /// <summary>The picker has to show both series, or one of them is invisible.</summary>
    [Fact]
    public async Task Metrics_WhenTwoChecksReportOneName_ListsBothWithTheirCheckNames()
    {
        var device = await CreateDeviceAsync();
        var icmp = await CreateCheckAsync(device.Id, "Icmp", "Reachability");
        var snmp = await CreateCheckAsync(device.Id, "Snmp", "CPU");
        var start = DateTimeOffset.UtcNow.AddMinutes(-3);

        await IngestAsync(BatchForCheck(device, icmp, "Reachability", start, latencyMs: 0.05));
        await IngestAsync(BatchForCheck(device, snmp, "CPU", start, latencyMs: 480d));

        var metrics = await GetAsync<List<MetricSummaryDto>>($"/api/monitored-devices/{device.Id}/metrics");
        var latency = metrics.Where(metric => metric.Metric == "check.latency_ms").ToList();

        Assert.Equal(2, latency.Count);
        Assert.Equal(["CPU", "Reachability"], latency.Select(metric => metric.CheckName).Order());
        Assert.Equal([icmp, snmp], latency.Select(metric => metric.CheckId).Order());
    }

    // ---- rollup and retention ----

    /// <summary>
    /// Readings spread over half an hour collapse into six five-minute buckets carrying the average,
    /// floor and ceiling of each. Refreshed by hand rather than waited for: the shipped policy runs
    /// every five minutes and a test must not depend on Timescale's scheduler having ticked.
    /// </summary>
    [Fact]
    public async Task ContinuousAggregate_AfterARefresh_ReturnsFiveMinuteBuckets()
    {
        var device = await CreateDeviceAsync();
        var checkId = Guid.CreateVersion7();

        // Aligned to a five-minute boundary so the readings cannot straddle a bucket, and recent
        // enough that the shipped thirty-day retention job cannot drop them between the write and
        // the read — this database is shared and Timescale's scheduler runs on its own clock.
        var start = AlignToBucket(DateTimeOffset.UtcNow.AddHours(-2));
        var values = Enumerable.Range(0, 30).Select(minute => (double)minute).ToArray();
        await IngestAsync(Batch(device, checkId, start, values, TimeSpan.FromMinutes(1)));

        await ExecuteAsync(
            "CALL refresh_continuous_aggregate('monitoring.device_metrics_5m', NULL, NULL);");

        var series = await GetAsync<MetricSeriesDto>(
            $"/api/monitored-devices/{device.Id}/metrics/series"
            + $"?metric=cpu.utilisation&from={Iso(start)}&to={Iso(start.AddMinutes(30))}"
            + "&resolution=FiveMinute&aggregation=Avg");

        Assert.Equal("FiveMinute", series.Resolution);
        Assert.Equal(300, series.BucketSeconds);
        Assert.Equal(6, series.Points.Count);
        Assert.All(series.Points, point => Assert.Equal(5, point.SampleCount));

        // First bucket holds minutes 0-4: average 2, floor 0, ceiling 4.
        var first = series.Points[0];
        Assert.Equal(2d, first.Value);
        Assert.Equal(0d, first.MinValue);
        Assert.Equal(4d, first.MaxValue);

        var max = await GetAsync<MetricSeriesDto>(
            $"/api/monitored-devices/{device.Id}/metrics/series"
            + $"?metric=cpu.utilisation&from={Iso(start)}&to={Iso(start.AddMinutes(30))}"
            + "&resolution=FiveMinute&aggregation=Max");
        Assert.Equal(4d, max.Points[0].Value);
    }

    /// <summary>
    /// The WP's third verification step, driven rather than waited for: the shipped retention job is
    /// re-pointed at an hour, run on demand, and the old chunk is gone while today's rows stay. It
    /// asserts against the policy the migration installed — not a hand-made one — so a migration that
    /// forgot to install it fails here.
    /// </summary>
    [Fact]
    public async Task RetentionPolicy_ShortenedAndRun_DropsOldDatedRowsAndKeepsRecentOnes()
    {
        var device = await CreateDeviceAsync();
        var checkId = Guid.CreateVersion7();
        // Three days back, not a year: old enough to be a chunk of its own, recent enough that the
        // shipped thirty-day policy will not have dropped it before this test looks.
        var stale = DateTimeOffset.UtcNow.AddDays(-3);
        var recent = DateTimeOffset.UtcNow.AddMinutes(-3);

        await IngestAsync(Batch(device, checkId, stale, [1d]));
        await IngestAsync(Batch(device, checkId, recent, [2d]));
        Assert.Equal(2, await CountMetricsAsync(device.Id, "cpu.utilisation"));

        // Retention drops whole chunks, so the cut-off has to leave the current chunk alone; an hour
        // does, because a chunk is a day wide and today's is not yet entirely older than that.
        await ExecuteAsync(
            """
            DO $$
            DECLARE job integer;
            BEGIN
                SELECT job_id INTO job FROM timescaledb_information.jobs
                WHERE proc_name = 'policy_retention' AND hypertable_name = 'device_metrics';
                PERFORM alter_job(job, config => jsonb_set(
                    (SELECT config FROM timescaledb_information.jobs WHERE job_id = job),
                    '{drop_after}', '"01:00:00"'));
                CALL run_job(job);
            END $$;
            """);

        Assert.Equal(1, await CountMetricsAsync(device.Id, "cpu.utilisation"));
        Assert.Equal(2d, await ScalarAsync<double>(
            $"SELECT value FROM monitoring.device_metrics WHERE device_id = '{device.Id}' "
            + "AND metric_name = 'cpu.utilisation'"));

        // Put the shipped policy back: this broker and database are shared by the whole collection.
        await ExecuteAsync(
            """
            DO $$
            DECLARE job integer;
            BEGIN
                SELECT job_id INTO job FROM timescaledb_information.jobs
                WHERE proc_name = 'policy_retention' AND hypertable_name = 'device_metrics';
                PERFORM alter_job(job, config => jsonb_set(
                    (SELECT config FROM timescaledb_information.jobs WHERE job_id = job),
                    '{drop_after}', '"30 days"'));
            END $$;
            """);
    }

    // ---- failure paths ----

    /// <summary>
    /// A truncated chart must never look like a complete one — the WP-2.3 traversal rule applied to a
    /// time range. Raw over a year is refused rather than silently downsampled or cut short.
    /// </summary>
    [Fact]
    public async Task Series_AtRawResolutionOverMoreThanADay_ReturnsValidationProblem()
    {
        var device = await CreateDeviceAsync();
        var to = DateTimeOffset.UtcNow;

        using var request = Authenticated(
            HttpMethod.Get,
            $"/api/monitored-devices/{device.Id}/metrics/series"
            + $"?metric=cpu.utilisation&from={Iso(to.AddDays(-30))}&to={Iso(to)}&resolution=Raw");
        using var response = await _client!.SendAsync(request);
        var problem = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("Raw resolution covers at most", problem, StringComparison.Ordinal);
    }

    /// <summary>The same range answers happily once the caller stops insisting on raw.</summary>
    [Fact]
    public async Task Series_OverThirtyDaysOnAuto_FallsBackToTheRollup()
    {
        var device = await CreateDeviceAsync();
        var to = DateTimeOffset.UtcNow;

        var series = await GetAsync<MetricSeriesDto>(
            $"/api/monitored-devices/{device.Id}/metrics/series"
            + $"?metric=cpu.utilisation&from={Iso(to.AddDays(-30))}&to={Iso(to)}");

        Assert.Equal("FiveMinute", series.Resolution);
    }

    [Fact]
    public async Task Series_WithNoMetricNamed_ReturnsValidationProblem()
    {
        var device = await CreateDeviceAsync();

        using var request = Authenticated(
            HttpMethod.Get, $"/api/monitored-devices/{device.Id}/metrics/series");
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Series_ForADeviceThatDoesNotExist_ReturnsNotFound()
    {
        using var request = Authenticated(
            HttpMethod.Get,
            $"/api/monitored-devices/{Guid.CreateVersion7()}/metrics/series?metric=cpu.utilisation");
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    /// <summary>Metrics are an agent surface, like every other monitoring endpoint.</summary>
    [Fact]
    public async Task Metrics_AsEndUser_AreForbidden()
    {
        var device = await CreateDeviceAsync();

        using var request = Authenticated(
            HttpMethod.Get, $"/api/monitored-devices/{device.Id}/metrics", "EndUser");
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---- fixtures ----

    private static DateTimeOffset AlignToBucket(DateTimeOffset value) =>
        new(value.Ticks - (value.Ticks % TimeSpan.FromMinutes(5).Ticks), value.Offset);

    private static string Iso(DateTimeOffset value) =>
        Uri.EscapeDataString(value.ToUniversalTime().ToString("O"));

    private static DeviceTelemetryReported Batch(
        DeviceDto device,
        Guid checkId,
        DateTimeOffset start,
        IReadOnlyList<double> values,
        TimeSpan? step = null)
    {
        var spacing = step ?? TimeSpan.FromSeconds(15);
        var results = values.Select((value, index) =>
        {
            var observedAt = start + (spacing * index);
            return new DeviceCheckResult(
                device.Id, device.CiId, checkId, "Snmp", "CPU", device.Address, observedAt,
                Succeeded: true, LatencyMs: 3.5, Error: null,
                Metrics: [new MetricSample("cpu.utilisation", value, null, "%")]);
        }).ToList();

        return new DeviceTelemetryReported(
            Guid.CreateVersion7(), start, "poller-1", "default", CycleNumber: 1, results);
    }

    /// <summary>A batch from one named check, carrying only the derived check.* facts.</summary>
    private static DeviceTelemetryReported BatchForCheck(
        DeviceDto device,
        Guid checkId,
        string checkName,
        DateTimeOffset observedAt,
        double latencyMs) => new(
        Guid.CreateVersion7(), observedAt, "poller-1", "default", CycleNumber: 1,
        [
            new DeviceCheckResult(
                device.Id, device.CiId, checkId, "Snmp", checkName, device.Address, observedAt,
                Succeeded: true, LatencyMs: latencyMs, Error: null, Metrics: []),
        ]);

    /// <summary>Creates a real check so the metric picker has a name to print.</summary>
    private async Task<Guid> CreateCheckAsync(Guid deviceId, string type, string name)
    {
        using var request = Authenticated(HttpMethod.Post, $"/api/monitored-devices/{deviceId}/checks");
        request.Content = JsonContent.Create(new
        {
            type,
            name,
            intervalSeconds = 60,
            timeoutSeconds = 5,
            parameters = type == "Snmp"
                ? new Dictionary<string, string> { ["oid"] = "1.3.6.1.2.1.1.3.0" }
                : null,
        });
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<CheckDto>();
        return created!.Id;
    }

    private sealed record CheckDto(Guid Id);

    private async Task IngestAsync(DeviceTelemetryReported telemetry)
    {
        await using var scope = _application.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IMetricIngestionService>()
            .IngestAsync(telemetry, CancellationToken.None);
    }

    private async Task<int> CountMetricsAsync(Guid deviceId, string metric)
    {
        await using var scope = _application.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<MonitoringDbContext>();
        return await context.DeviceMetrics
            .CountAsync(row => row.DeviceId == deviceId && row.MetricName == metric);
    }

    /// <summary>
    /// Straight ADO rather than <c>ExecuteSqlRawAsync</c>: EF treats the SQL as a composite format
    /// string, and every one of these statements is a <c>DO $$ ... $$</c> block full of braces.
    /// </summary>
    private async Task ExecuteAsync(string sql)
    {
        await using var scope = _application.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<MonitoringDbContext>();
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var scope = _application.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<MonitoringDbContext>();
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync();
        return (T)value!;
    }

    private async Task<DeviceDto> CreateDeviceAsync()
    {
        var ci = await CreateCiAsync();
        using var request = Authenticated(HttpMethod.Post, "/api/monitored-devices");
        request.Content = JsonContent.Create(new
        {
            ciId = ci.Id,
            address = "10.30.0.1",
            pollerGroup = $"group-{Guid.NewGuid():N}"[..20],
        });
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return Assert.IsType<DeviceDto>(await response.Content.ReadFromJsonAsync<DeviceDto>());
    }

    private async Task<CiDto> CreateCiAsync()
    {
        using var request = Authenticated(HttpMethod.Post, "/api/cis");
        request.Content = JsonContent.Create(new
        {
            type = "NetworkDevice",
            name = $"Switch {Guid.NewGuid():N}",
            attributes = new Dictionary<string, string>
            {
                ["managementIp"] = "10.0.0.1",
                ["vendor"] = "Cisco",
                ["portCount"] = "48",
            },
        });
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return Assert.IsType<CiDto>(await response.Content.ReadFromJsonAsync<CiDto>());
    }

    private async Task<T> GetAsync<T>(string uri, string role = "Technician")
    {
        using var request = Authenticated(HttpMethod.Get, uri, role);
        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Assert.IsType<T>(await response.Content.ReadFromJsonAsync<T>());
    }

    private static HttpRequestMessage Authenticated(HttpMethod method, string uri, string role = "Technician")
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Add(MetricsAuthenticationHandler.RoleHeader, role);
        return request;
    }

    private sealed record CiDto(Guid Id, string Type, string Name);

    private sealed record DeviceDto(Guid Id, Guid CiId, string Address, string PollerGroup);

    private sealed record MetricPointDto(
        DateTimeOffset Timestamp,
        double Value,
        double MinValue,
        double MaxValue,
        long SampleCount);

    private sealed record MetricSeriesDto(
        Guid DeviceId,
        string Metric,
        Guid? CheckId,
        string? Unit,
        string Resolution,
        string Aggregation,
        int BucketSeconds,
        List<MetricPointDto> Points);

    private sealed record MetricSummaryDto(
        string Metric,
        string? Unit,
        Guid CheckId,
        string? CheckName,
        DateTimeOffset LastObservedAt,
        double LastValue);

    private sealed record InventoryEntryDto(string Name, string Value, DateTimeOffset ObservedAt);

    private sealed record InventoryDto(Guid DeviceId, List<InventoryEntryDto> Facts);

    private sealed class MetricsApplication : WebApplicationFactory<Program>
    {
        private readonly string _connectionString;
        private readonly string _rabbitMqConnectionString;
        private readonly string _minioConnectionString;

        public MetricsApplication(
            string connectionString,
            string rabbitMqConnectionString,
            string minioConnectionString)
        {
            _connectionString = connectionString;
            _rabbitMqConnectionString = rabbitMqConnectionString;
            _minioConnectionString = minioConnectionString;
            Environment.SetEnvironmentVariable("ConnectionStrings__database", connectionString);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Authentication:Authority"] = "https://identity.example.test/realms/it-platform",
                    ["Authentication:Audience"] = "it-platform-api",
                    ["Authentication:ClientId"] = "it-platform-web",
                    ["Authentication:PostLogoutRedirectUri"] = "https://app.example.test/",
                    ["ConnectionStrings:database"] = _connectionString,
                    ["ConnectionStrings:rabbitmq"] = _rabbitMqConnectionString,
                    ["ConnectionStrings:minio"] = _minioConnectionString,
                    ["ObjectStorage:AccessKey"] = "minioadmin",
                    ["ObjectStorage:SecretKey"] = "minio-test-password",
                    ["Platform:ApplyMigrations"] = "false",
                    // No bus here: this class tests storage and the query API. The consumer that puts
                    // telemetry through the broker is PollerTelemetryBusIntegrationTests' job, and a
                    // second MassTransit host against the shared broker is what WP-3.2 got bitten by.
                    ["Platform:EnableMessageBus"] = "false",
                    ["Platform:EnableScheduler"] = "false",
                }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.AddAuthentication(options =>
                    {
                        options.DefaultAuthenticateScheme = MetricsAuthenticationHandler.TestScheme;
                        options.DefaultChallengeScheme = MetricsAuthenticationHandler.TestScheme;
                        options.DefaultForbidScheme = MetricsAuthenticationHandler.TestScheme;
                    })
                    .AddScheme<AuthenticationSchemeOptions, MetricsAuthenticationHandler>(
                        MetricsAuthenticationHandler.TestScheme,
                        _ => { });
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            Environment.SetEnvironmentVariable("ConnectionStrings__database", null);
        }
    }

    private sealed class MetricsAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string TestScheme = "MetricsTest";
        public const string RoleHeader = "X-Test-Role";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var role = Request.Headers[RoleHeader].ToString();
            if (string.IsNullOrWhiteSpace(role))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, "metrics-test-user-id"),
                    new Claim(ClaimTypes.Name, "metrics-test-user"),
                    new Claim(ClaimTypes.Role, role),
                ],
                TestScheme);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), TestScheme)));
        }
    }
}
