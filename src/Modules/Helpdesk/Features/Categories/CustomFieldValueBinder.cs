using System.Globalization;

using Modules.Helpdesk.Data;

namespace Modules.Helpdesk.Features.Categories;

public sealed record CustomFieldBindResult(
    IReadOnlyDictionary<Guid, string> Values,
    IReadOnlyDictionary<string, string[]> Errors);

/// <summary>
/// Validates and canonicalises the custom-field values submitted with a ticket against the
/// field definitions of the ticket's category. Pure logic — no database access.
/// </summary>
public static class CustomFieldValueBinder
{
    public const string DateFormat = "yyyy-MM-dd";

    public static CustomFieldBindResult Bind(
        IReadOnlyList<TicketCustomField> fields,
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
                errors[ErrorKey(key)] = [$"'{key}' is not a field of the selected category."];
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

    private static bool TryCanonicalise(TicketCustomField field, string raw, out string canonical, out string error)
    {
        canonical = raw;
        error = string.Empty;
        switch (field.Type)
        {
            case CustomFieldType.Text:
                if (raw.Length > 1_000)
                {
                    error = $"{field.Label} must be 1000 characters or fewer.";
                    return false;
                }

                return true;
            case CustomFieldType.Number:
                if (!decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var number))
                {
                    error = $"{field.Label} must be a number.";
                    return false;
                }

                canonical = number.ToString(CultureInfo.InvariantCulture);
                return true;
            case CustomFieldType.Date:
                if (!DateOnly.TryParseExact(raw, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                {
                    error = $"{field.Label} must be a date in {DateFormat} format.";
                    return false;
                }

                canonical = date.ToString(DateFormat, CultureInfo.InvariantCulture);
                return true;
            case CustomFieldType.Select:
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
