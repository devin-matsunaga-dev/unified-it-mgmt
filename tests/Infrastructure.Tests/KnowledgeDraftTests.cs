using Modules.Helpdesk.Data;
using Modules.Helpdesk.Features.Problems;

namespace Infrastructure.Tests;

/// <summary>
/// The article draft a closing problem is prompted with (WP-5.7), composed with no database.
/// <para>
/// The part worth testing is what it does with the incident titles. Eleven incidents about one switch are
/// usually three sentences said eleven times, and a draft that lists all eleven is one nobody finishes.
/// </para>
/// </summary>
public sealed class KnowledgeDraftTests
{
    [Fact]
    public void Compose_CarriesEverythingSomebodyAlreadyTyped()
    {
        var problem = Problem();
        problem.RootCause = "  A failing uplink SFP on port 23.  ";
        problem.Workaround = "Move the affected users to port 24.";
        problem.Resolution = "Replaced the SFP.";

        var draft = KnowledgeDraft.Compose(problem, "HQ floor 2 switch", [Incident("INC-000001", "Wi-Fi drops")]);

        Assert.Equal(problem.Id, draft.ProblemId);
        Assert.Equal("PRB-000000", draft.ProblemNumber);
        Assert.Equal(problem.Title, draft.Title);
        Assert.Equal("HQ floor 2 switch", draft.SubjectName);
        // Trimmed, because the draft is text somebody is about to paste into an article.
        Assert.Equal("A failing uplink SFP on port 23.", draft.RootCause);
        Assert.Equal("Move the affected users to port 24.", draft.Workaround);
        Assert.Equal("Replaced the SFP.", draft.Resolution);
    }

    /// <summary>
    /// A field nobody filled in comes back null rather than empty, because the prompt's whole job is to
    /// show what is still missing and an empty string reads as something somebody wrote.
    /// </summary>
    [Fact]
    public void Compose_ForAProblemWithNoCauseRecorded_LeavesTheFieldNull()
    {
        var problem = Problem();
        problem.RootCause = "   ";

        var draft = KnowledgeDraft.Compose(problem, null, []);

        Assert.Null(draft.RootCause);
        Assert.Null(draft.Workaround);
        Assert.Null(draft.SubjectName);
        Assert.Empty(draft.Symptoms);
        Assert.Empty(draft.IncidentNumbers);
    }

    /// <summary>The grouping, which is the reason this is composition and not string concatenation.</summary>
    [Fact]
    public void Compose_DeduplicatesRepeatedSymptomsAndCountsThem()
    {
        var draft = KnowledgeDraft.Compose(Problem(), "HQ floor 2 switch",
        [
            Incident("INC-000001", "Wi-Fi keeps dropping"),
            Incident("INC-000002", "wi-fi keeps dropping"),
            Incident("INC-000003", "Wi-Fi keeps dropping"),
            Incident("INC-000004", "Video calls cut out"),
        ]);

        Assert.Collection(
            draft.Symptoms,
            symptom =>
            {
                Assert.Equal("Wi-Fi keeps dropping", symptom.Text);
                Assert.Equal(3, symptom.IncidentCount);
            },
            symptom =>
            {
                Assert.Equal("Video calls cut out", symptom.Text);
                Assert.Equal(1, symptom.IncidentCount);
            });
    }

    /// <summary>
    /// Frequency first, then alphabetical. The tiebreak exists so that the same problem always produces
    /// the same draft — a prompt that reshuffles itself between two openings is one nobody trusts.
    /// </summary>
    [Fact]
    public void Compose_OrdersSymptomsByFrequencyThenAlphabetically()
    {
        var draft = KnowledgeDraft.Compose(Problem(), null,
        [
            Incident("INC-000001", "Zebra symptom"),
            Incident("INC-000002", "Apple symptom"),
            Incident("INC-000003", "Middle symptom"),
            Incident("INC-000004", "Middle symptom"),
        ]);

        Assert.Equal(
            new[] { "Middle symptom", "Apple symptom", "Zebra symptom" },
            draft.Symptoms.Select(symptom => symptom.Text).ToArray());
    }

    [Fact]
    public void Compose_WithMoreDistinctSymptomsThanTheCap_KeepsTheMostReported()
    {
        var incidents = Enumerable.Range(1, KnowledgeDraft.MaxSymptoms + 5)
            .Select(index => Incident($"INC-{index:000000}", $"Symptom {index:00}"))
            .Append(Incident("INC-000900", "The one everybody reported"))
            .Append(Incident("INC-000901", "The one everybody reported"))
            .ToArray();

        var draft = KnowledgeDraft.Compose(Problem(), null, incidents);

        Assert.Equal(KnowledgeDraft.MaxSymptoms, draft.Symptoms.Count);
        Assert.Equal("The one everybody reported", draft.Symptoms[0].Text);
        // Every incident is still named, even where its symptom did not make the list — the article is
        // shortened, the evidence behind it is not.
        Assert.Equal(incidents.Length, draft.IncidentNumbers.Count);
    }

    [Fact]
    public void Compose_ListsIncidentNumbersInOrder()
    {
        var draft = KnowledgeDraft.Compose(Problem(), null,
        [
            Incident("INC-000009", "Later"),
            Incident("INC-000002", "Earlier"),
        ]);

        Assert.Equal(new[] { "INC-000002", "INC-000009" }, draft.IncidentNumbers.ToArray());
    }

    /// <summary>An incident with a blank title contributes no symptom rather than a blank line.</summary>
    [Fact]
    public void Compose_IgnoresBlankIncidentTitles()
    {
        var draft = KnowledgeDraft.Compose(Problem(), null,
        [
            Incident("INC-000001", "   "),
            Incident("INC-000002", "A real symptom"),
        ]);

        Assert.Equal("A real symptom", Assert.Single(draft.Symptoms).Text);
        Assert.Equal(2, draft.IncidentNumbers.Count);
    }

    private static Problem Problem() => new()
    {
        Id = Guid.CreateVersion7(),
        Title = "Recurring drops on the second floor switch",
        Description = "Five incidents in a week.",
        Status = ProblemStatus.Closed,
        Priority = TicketPriority.High,
    };

    private static ProblemIncidentResponse Incident(string number, string title) => new(
        Guid.CreateVersion7(),
        number,
        title,
        "Resolved",
        TicketPriority.Medium,
        DateTimeOffset.UtcNow.AddDays(-2),
        "technician1",
        "Technician One",
        DateTimeOffset.UtcNow.AddDays(-2));
}
