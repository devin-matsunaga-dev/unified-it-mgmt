using Modules.Monitoring.Data;

namespace Modules.Monitoring.Features.Interfaces;

/// <param name="IfIndex">The device's own index, and what every one of this interface's metric names carries.</param>
/// <param name="MetricPrefix">
/// What to prepend to a field name to get a series for this interface — <c>interface.3.</c>. Sent
/// rather than left to the browser to build, so the shape of a metric name is knowledge one module
/// holds and not three.
/// </param>
/// <param name="UtilisationPercent">
/// The busier direction as a percentage of the link's speed, or null where the poller has no rate
/// yet or the device reports no speed. Deliberately can exceed 100: that means the speed is wrong.
/// </param>
public sealed record DeviceInterfaceResponse(
    int IfIndex,
    string Name,
    string? Alias,
    string? MacAddress,
    int? InterfaceType,
    string AdminStatus,
    string OperStatus,
    long? SpeedBitsPerSecond,
    double? BitsInPerSecond,
    double? BitsOutPerSecond,
    double? UtilisationPercent,
    double? ErrorsInPerSecond,
    double? ErrorsOutPerSecond,
    double? DiscardsInPerSecond,
    double? DiscardsOutPerSecond,
    Guid CheckId,
    string MetricPrefix,
    DateTimeOffset ObservedAt)
{
    public static DeviceInterfaceResponse From(DeviceInterface link) =>
        new(
            link.IfIndex,
            // An interface with no name at all is a row the device numbered and never labelled;
            // showing its index is more use than showing a blank cell.
            link.Name ?? $"Interface {link.IfIndex}",
            link.Alias,
            link.MacAddress,
            link.InterfaceType,
            link.AdminStatus.ToString(),
            link.OperStatus.ToString(),
            link.SpeedBitsPerSecond,
            link.BitsInPerSecond,
            link.BitsOutPerSecond,
            link.UtilisationPercent,
            link.ErrorsInPerSecond,
            link.ErrorsOutPerSecond,
            link.DiscardsInPerSecond,
            link.DiscardsOutPerSecond,
            link.CheckId,
            InterfaceMetricNames.For(link.IfIndex, string.Empty),
            link.ObservedAt);
}
