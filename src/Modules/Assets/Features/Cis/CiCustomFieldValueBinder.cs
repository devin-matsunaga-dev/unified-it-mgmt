using System.Globalization;

using Modules.Assets.Data;

namespace Modules.Assets.Features.Cis;

public sealed record CiCustomFieldBindResult(
    IReadOnlyDictionary<Guid, string> Values,
    IReadOnlyDictionary<string, string[]> Errors);

/// <summary>
/// Validates and canonicalises the user-defined field values submitted with a CI against the field
/// definitions of the CI's type. Pure logic — no database access.
/// </summary>
public static class CiCustomFieldValueBinder
{
    public const string DateFormat = "yyyy-MM-dd";

    public static CiCustomFieldBindResult Bind(
        IReadOnlyList<CiCustomField> fields,
        IReadOnlyDictionary<string, string?>? submitted)
    {
        var values = new Dictionary<Guid, string>();
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var provided = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in submitted ?? new Dictionary<string, string?>())
        {
            provided[entry.Key.Trim()] = entry.Value;
        }

        foreach (var key in provided.Keys)
        {
            if (!fields.Any(field => string.Equals(field.Key, key, StringComparison.OrdinalIgnoreCase)))
            {
                errors[ErrorKey(key)] = [$"'{key}' is not a field of the selected CI type."];
            }
        }

        foreach (var field in fields)
        {
            var raw = provided.TryGetValue(field.Key, out var submittedValue) ? submittedValue?.Trim() : null;
            if (string.IsNullOrEmpty(raw))
            {
                if (field.IsRequired)
                {
                    errors[ErrorKey(field.Key)] = [$"{field.Label} is required."];
                }

                continue;
            }

            if (TryCanonicalise(field, raw, out var canonical, out var error))
            {
                values[field.Id] = canonical;
            }
            else
            {
                errors[ErrorKey(field.Key)] = [error];
            }
        }

        return new(values, errors);
    }

    private static bool TryCanonicalise(CiCustomField field, string raw, out string canonical, out string error)
    {
        canonical = raw;
        error = string.Empty;
        switch (field.Type)
        {
            case CiCustomFieldType.Text:
                if (raw.Length > 1_000)
                {
                    error = $"{field.Label} must be 1000 characters or fewer.";
                    return false;
                }

                return true;
            case CiCustomFieldType.Number:
                if (!decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var number))
                {
                    error = $"{field.Label} must be a number.";
                    return false;
                }

                canonical = number.ToString(CultureInfo.InvariantCulture);
                return true;
            case CiCustomFieldType.Date:
                if (!DateOnly.TryParseExact(raw, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                {
                    error = $"{field.Label} must be a date in {DateFormat} format.";
                    return false;
                }

                canonical = date.ToString(DateFormat, CultureInfo.InvariantCulture);
                return true;
            case CiCustomFieldType.Select:
                var option = field.Options.FirstOrDefault(item => string.Equals(item, raw, StringComparison.OrdinalIgnoreCase));
                if (option is null)
                {
                    error = $"{field.Label} must be one of: {string.Join(", ", field.Options)}.";
                    return false;
                }

                canonical = option;
                return true;
            default:
                throw new InvalidOperationException($"Unknown custom field type '{field.Type}'.");
        }
    }

    private static string ErrorKey(string fieldKey) => $"customFields.{fieldKey}";
}
