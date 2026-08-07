using System.Globalization;
using System.Net;

using Modules.Assets.Data;

namespace Modules.Assets.Features.Cis;

public sealed record CiAttributeDefinition(
    string Key,
    string Label,
    CiAttributeKind Kind,
    bool IsRequired);

public enum CiAttributeKind
{
    Text = 1,
    Integer = 2,
    IpAddress = 3,
}

public sealed record CiAttributeBindResult(
    IReadOnlyDictionary<string, string> Values,
    IReadOnlyDictionary<string, string[]> Errors);

/// <summary>
/// The single description of which fixed attributes each CI type carries. TPH makes the underlying
/// columns nullable, so this is what actually enforces "type-specific fields enforced" — the schema
/// drives validation on write, projection on read, and the field list the form renders.
/// Pure logic — no database access.
/// </summary>
public static class CiTypeSchema
{
    private static readonly IReadOnlyDictionary<CiType, IReadOnlyList<CiAttributeDefinition>> Definitions =
        new Dictionary<CiType, IReadOnlyList<CiAttributeDefinition>>
        {
            [CiType.Hardware] =
            [
                new("manufacturer", "Manufacturer", CiAttributeKind.Text, true),
                new("model", "Model", CiAttributeKind.Text, true),
            ],
            [CiType.Server] =
            [
                new("hostname", "Hostname", CiAttributeKind.Text, true),
                new("operatingSystem", "Operating system", CiAttributeKind.Text, true),
                new("cpuCores", "CPU cores", CiAttributeKind.Integer, true),
                new("ramGb", "RAM (GB)", CiAttributeKind.Integer, true),
            ],
            [CiType.NetworkDevice] =
            [
                new("managementIp", "Management IP", CiAttributeKind.IpAddress, true),
                new("vendor", "Vendor", CiAttributeKind.Text, true),
                new("portCount", "Port count", CiAttributeKind.Integer, true),
            ],
            [CiType.Software] =
            [
                new("vendor", "Vendor", CiAttributeKind.Text, true),
                new("version", "Version", CiAttributeKind.Text, true),
            ],
            [CiType.Virtual] =
            [
                new("hostname", "Hostname", CiAttributeKind.Text, true),
                new("hypervisor", "Hypervisor", CiAttributeKind.Text, true),
                new("vcpuCores", "vCPU cores", CiAttributeKind.Integer, true),
                new("ramGb", "RAM (GB)", CiAttributeKind.Integer, true),
            ],
            [CiType.Logical] =
            [
                new("purpose", "Purpose", CiAttributeKind.Text, true),
                new("serviceTier", "Service tier", CiAttributeKind.Text, false),
            ],
        };

    public static IReadOnlyList<CiAttributeDefinition> For(CiType type) =>
        Definitions.TryGetValue(type, out var definitions)
            ? definitions
            : throw new InvalidOperationException($"Unknown CI type '{type}'.");

    public static IReadOnlyDictionary<CiType, IReadOnlyList<CiAttributeDefinition>> All => Definitions;

    /// <summary>
    /// Validates and canonicalises the attributes submitted for a CI against its type's schema.
    /// Attributes belonging to another type are rejected rather than ignored, so a form that posts
    /// the wrong shape fails loudly instead of silently dropping data.
    /// </summary>
    public static CiAttributeBindResult Bind(CiType type, IReadOnlyDictionary<string, string?>? submitted)
    {
        var definitions = For(type);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var provided = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in submitted ?? new Dictionary<string, string?>())
        {
            provided[entry.Key.Trim()] = entry.Value;
        }

        foreach (var key in provided.Keys)
        {
            if (!definitions.Any(definition => string.Equals(definition.Key, key, StringComparison.OrdinalIgnoreCase)))
            {
                errors[ErrorKey(key)] = [$"'{key}' is not an attribute of a {type} CI."];
            }
        }

        foreach (var definition in definitions)
        {
            var raw = provided.TryGetValue(definition.Key, out var submittedValue) ? submittedValue?.Trim() : null;
            if (string.IsNullOrEmpty(raw))
            {
                if (definition.IsRequired)
                {
                    errors[ErrorKey(definition.Key)] = [$"{definition.Label} is required for a {type} CI."];
                }

                continue;
            }

            if (TryCanonicalise(definition, raw, out var canonical, out var error))
            {
                values[definition.Key] = canonical;
            }
            else
            {
                errors[ErrorKey(definition.Key)] = [error];
            }
        }

        return new(values, errors);
    }

    private static bool TryCanonicalise(
        CiAttributeDefinition definition,
        string raw,
        out string canonical,
        out string error)
    {
        canonical = raw;
        error = string.Empty;
        switch (definition.Kind)
        {
            case CiAttributeKind.Text:
                if (raw.Length > 500)
                {
                    error = $"{definition.Label} must be 500 characters or fewer.";
                    return false;
                }

                return true;
            case CiAttributeKind.Integer:
                if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
                    || number < 0)
                {
                    error = $"{definition.Label} must be a whole number of zero or more.";
                    return false;
                }

                canonical = number.ToString(CultureInfo.InvariantCulture);
                return true;
            case CiAttributeKind.IpAddress:
                if (!IPAddress.TryParse(raw, out var address))
                {
                    error = $"{definition.Label} must be a valid IPv4 or IPv6 address.";
                    return false;
                }

                canonical = address.ToString();
                return true;
            default:
                throw new InvalidOperationException($"Unknown CI attribute kind '{definition.Kind}'.");
        }
    }

    private static string ErrorKey(string attributeKey) => $"attributes.{attributeKey}";
}
