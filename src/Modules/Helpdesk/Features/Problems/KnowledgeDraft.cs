using Modules.Helpdesk.Data;

namespace Modules.Helpdesk.Features.Problems;

/// <summary>
/// Turns a closed problem into the article somebody would write about it.
/// <para>
/// The WP asks that closing a problem prompt for a knowledge article. WP-5.9 owns the knowledge base and
/// this package stores nothing — so what is offered is a draft, composed entirely from fields the person
/// closing the problem has just finished writing. It exists as pure composition rather than inside the
/// service because the interesting part is what it does with the incident titles, and that is worth
/// testing without a database.
/// </para>
/// </summary>
public static class KnowledgeDraft
{
    /// <summary>Distinct symptoms to carry. Beyond this the draft stops being something anybody edits.</summary>
    public const int MaxSymptoms = 10;

    public static KnowledgeDraftResponse Compose(
        Problem problem,
        string? subjectName,
        IReadOnlyCollection<ProblemIncidentResponse> incidents)
    {
        ArgumentNullException.ThrowIfNull(problem);
        ArgumentNullException.ThrowIfNull(incidents);

        return new KnowledgeDraftResponse(
            problem.Id,
            problem.Number,
            problem.Title,
            subjectName,
            Symptoms(incidents),
            Blank(problem.RootCause),
            Blank(problem.Workaround),
            Blank(problem.Resolution),
            [.. incidents.OrderBy(incident => incident.Number, StringComparer.Ordinal)
                .Select(incident => incident.Number)]);
    }

    /// <summary>
    /// What people actually reported, deduplicated and counted.
    /// <para>
    /// Eleven incidents about one switch are usually three sentences said eleven times, and an article
    /// listing all eleven is one nobody finishes reading. The count travels with each line because
    /// "reported nine times" is the sentence that makes a symptom worth putting first — and the order is
    /// frequency then alphabetical, so the same problem always produces the same draft.
    /// </para>
    /// </summary>
    private static IReadOnlyList<KnowledgeDraftSymptom> Symptoms(IReadOnlyCollection<ProblemIncidentResponse> incidents) =>
        [.. incidents
            .Select(incident => incident.Title.Trim())
            .Where(title => title.Length > 0)
            .GroupBy(title => title, StringComparer.OrdinalIgnoreCase)
            .Select(group => new KnowledgeDraftSymptom(group.First(), group.Count()))
            .OrderByDescending(symptom => symptom.IncidentCount)
            .ThenBy(symptom => symptom.Text, StringComparer.OrdinalIgnoreCase)
            .Take(MaxSymptoms)];

    /// <summary>
    /// Whitespace becomes null. A draft field that is present but empty reads to the browser as something
    /// somebody wrote, and the prompt's whole job is to show what is still missing.
    /// </summary>
    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
