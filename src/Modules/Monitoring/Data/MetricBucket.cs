namespace Modules.Monitoring.Data;

/// <summary>
/// One point of a metric series: a time bucket and what the readings inside it did. Keyless — it is
/// the shape of a query result, never a table, and the same shape whether it came from the raw
/// hypertable or from the five-minute continuous aggregate, so a chart never has to know which
/// resolution answered it.
/// </summary>
public sealed class MetricBucket
{
    public DateTimeOffset Bucket { get; set; }

    public double AvgValue { get; set; }

    public double MinValue { get; set; }

    public double MaxValue { get; set; }

    /// <summary>How many raw readings the bucket covers. Always 1 at raw resolution.</summary>
    public long SampleCount { get; set; }
}
