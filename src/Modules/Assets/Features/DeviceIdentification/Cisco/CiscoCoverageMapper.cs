using System.Text.Json;

namespace Modules.Assets.Features.DeviceIdentification.Cisco;

/// <summary>
/// Reads Cisco's SN2INFO coverage response. **Written against Cisco's published DevNet documentation
/// rather than a sample**, which is possible here and was not for Dell: Cisco documents both the
/// endpoint and the response structure openly.
/// <para>
/// The documented shape:
/// <code>
/// { "serial_numbers": [ {
///     "sr_no": "...",
///     "is_covered": "YES" | "NO",
///     "orderable_pid_list": [ { "orderable_pid": "...", "item_description": "..." } ],
///     "warranty_end_date": "YYYY-MM-DD",
///     "coverage_end_date": "YYYY-MM-DD" } ] }
/// </code>
/// </para>
/// <para>
/// Every read is defensive. A device Cisco does not know comes back as an entry with no PID list
/// rather than as an error, and a response that does not match at all must produce an unidentified
/// device rather than an exception — a technician has to be able to register the switch either way.
/// </para>
/// </summary>
public interface ICiscoCoverageMapper
{
    DeviceIdentificationResult? Map(JsonElement body, string serialNumber);
}

public sealed class CiscoCoverageMapper : ICiscoCoverageMapper
{
    public DeviceIdentificationResult? Map(JsonElement body, string serialNumber)
    {
        if (body.ValueKind is not JsonValueKind.Object) return null;
        if (!body.TryGetProperty("serial_numbers", out var serials)
            || serials.ValueKind is not JsonValueKind.Array) return null;

        // Matched on the serial rather than taken positionally: the API accepts several at once, and
        // this provider asks about one — but a response that returned them in another order would
        // otherwise attach one device's model to another's.
        var entry = serials.EnumerateArray().FirstOrDefault(item =>
            item.ValueKind is JsonValueKind.Object
            && item.TryGetProperty("sr_no", out var number)
            && string.Equals(number.GetString(), serialNumber, StringComparison.OrdinalIgnoreCase));
        if (entry.ValueKind is not JsonValueKind.Object) return null;

        if (!entry.TryGetProperty("orderable_pid_list", out var pids)
            || pids.ValueKind is not JsonValueKind.Array) return null;

        var pid = pids.EnumerateArray().FirstOrDefault(item =>
            item.ValueKind is JsonValueKind.Object
            && item.TryGetProperty("orderable_pid", out var value)
            && !string.IsNullOrWhiteSpace(value.GetString()));
        if (pid.ValueKind is not JsonValueKind.Object) return null;

        var orderablePid = Text(pid, "orderable_pid");
        if (orderablePid is null) return null;

        return new DeviceIdentificationResult
        {
            Manufacturer = "Cisco",
            // The item description is what a person would recognise — "Catalyst 6500 Supervisor" —
            // where the PID is the orderable code. Falling back to the PID keeps the model field
            // populated with something true rather than empty.
            Model = Text(pid, "item_description") ?? orderablePid,
            ProductNumber = orderablePid,
            SerialNumber = serialNumber,
            DeviceType = "NetworkDevice",
            Source = "Cisco",
            // An exact serial match against the manufacturer's own record. Nothing is inferred.
            Confidence = IdentificationConfidence.High,
        };
    }

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value)
        && value.ValueKind is JsonValueKind.String
        && value.GetString() is { Length: > 0 } text
            ? text
            : null;
}
