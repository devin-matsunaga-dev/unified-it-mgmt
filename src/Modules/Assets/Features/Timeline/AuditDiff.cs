using System.Text.Json;

namespace Modules.Assets.Features.Timeline;

/// <summary>
/// Which fields an audited edit actually changed, worked out by comparing the before and after documents
/// the audit log stored.
/// <para>
/// The audit log keeps whole-entity snapshots rather than field deltas, which is right for an audit — a
/// snapshot can be read back years later without the code that wrote it — and useless on a timeline,
/// where "Updated" against a CI six times in a row says nothing at all. This is the one place that turns
/// the pair back into a sentence.
/// </para>
/// <para>
/// Pure and total: malformed or absent JSON produces no field names rather than an exception. A timeline
/// that failed because an old audit row held something unexpected would lose every other event with it.
/// </para>
/// </summary>
public static class AuditDiff
{
    /// <summary>The most field names one entry will name before it starts counting instead.</summary>
    public const int MaximumNamedFields = 6;

    /// <summary>
    /// The dotted paths whose values differ between the two documents, in document order.
    /// <para>
    /// Nested objects are walked, so an owner change reads <c>ownership.ownerName</c> rather than
    /// <c>ownership</c>. Arrays are compared whole and named at their own path: the log holds no identity
    /// for their elements, so "the third custom field changed" is a claim this cannot honestly make.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> ChangedFields(string? beforeJson, string? afterJson)
    {
        // A creation has no before and a deletion has no after. Neither is a change to some of the
        // fields — it is the whole record arriving or leaving — so neither names any.
        if (string.IsNullOrWhiteSpace(beforeJson) || string.IsNullOrWhiteSpace(afterJson))
        {
            return [];
        }

        JsonElement before;
        JsonElement after;
        try
        {
            before = JsonDocument.Parse(beforeJson).RootElement.Clone();
            after = JsonDocument.Parse(afterJson).RootElement.Clone();
        }
        catch (JsonException)
        {
            return [];
        }

        if (before.ValueKind != JsonValueKind.Object || after.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        var changed = new List<string>();
        Compare(before, after, prefix: null, changed);
        return changed;
    }

    /// <summary>
    /// The changed fields as one clause, or null when an edit changed nothing readable.
    /// <para>
    /// Capped, because an import that rewrote thirty attributes is a sentence nobody finishes. The cap
    /// counts what it left out rather than trailing off, so the row still states the size of the edit.
    /// </para>
    /// </summary>
    public static string? Describe(IReadOnlyList<string> changedFields)
    {
        ArgumentNullException.ThrowIfNull(changedFields);

        if (changedFields.Count == 0)
        {
            return null;
        }

        var named = string.Join(", ", changedFields.Take(MaximumNamedFields));
        return changedFields.Count <= MaximumNamedFields
            ? $"Changed {named}."
            : $"Changed {named} and {changedFields.Count - MaximumNamedFields} more.";
    }

    private static void Compare(JsonElement before, JsonElement after, string? prefix, List<string> changed)
    {
        foreach (var property in before.EnumerateObject())
        {
            var path = prefix is null ? property.Name : $"{prefix}.{property.Name}";

            // A property the newer document does not have at all is a shape change rather than an edit —
            // the two snapshots were written by different versions of the record. Named once, not walked.
            if (!after.TryGetProperty(property.Name, out var updated))
            {
                changed.Add(path);
                continue;
            }

            if (property.Value.ValueKind == JsonValueKind.Object
                && updated.ValueKind == JsonValueKind.Object)
            {
                Compare(property.Value, updated, path, changed);
                continue;
            }

            // Compared on the raw text, which is what the log actually holds. Two documents that
            // serialise a number differently would read as a change, and that is the honest answer: the
            // stored records genuinely differ.
            if (!string.Equals(property.Value.GetRawText(), updated.GetRawText(), StringComparison.Ordinal))
            {
                changed.Add(path);
            }
        }

        // Properties only the newer document has: a field that was added to the record.
        foreach (var property in after.EnumerateObject())
        {
            if (!before.TryGetProperty(property.Name, out _))
            {
                changed.Add(prefix is null ? property.Name : $"{prefix}.{property.Name}");
            }
        }
    }
}
