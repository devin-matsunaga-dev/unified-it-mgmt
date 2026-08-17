using Modules.Assets.Data;
using Modules.Assets.Features.Changes;

namespace Infrastructure.Tests;

/// <summary>
/// The WP-5.8 change lifecycle, proved without infrastructure — the workflow is a table and a function,
/// so every rule in it is a call rather than a database.
/// </summary>
public sealed class ChangeWorkflowTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 17, 9, 0, 0, TimeSpan.Zero);

    private const string Requester = "requester-id";
    private const string Approver = "approver-id";

    [Fact]
    public void NextFrom_Draft_OffersSubmitAndCancelOnly()
    {
        Assert.Equal(
            [ChangeRequestStatus.Submitted, ChangeRequestStatus.Cancelled],
            ChangeWorkflow.NextFrom(ChangeRequestStatus.Draft));
    }

    /// <summary>
    /// Approved is terminal, and it is the one state where that matters: the approval has already left
    /// the module as an event and a maintenance window exists because of it.
    /// </summary>
    [Theory]
    [InlineData(ChangeRequestStatus.Approved)]
    [InlineData(ChangeRequestStatus.Rejected)]
    [InlineData(ChangeRequestStatus.Cancelled)]
    public void NextFrom_ATerminalState_OffersNothing(ChangeRequestStatus status) =>
        Assert.Empty(ChangeWorkflow.NextFrom(status));

    [Fact]
    public void Check_DraftToSubmitted_WithCis_IsAllowed() =>
        Assert.Equal(
            ChangeTransitionVerdict.Allowed,
            ChangeWorkflow.Check(Change(), ChangeRequestStatus.Submitted, ciCount: 1, Requester, Now));

    /// <summary>A change that names nothing would produce a maintenance window covering nothing.</summary>
    [Fact]
    public void Check_DraftToSubmitted_WithNoCis_NeedsCis() =>
        Assert.Equal(
            ChangeTransitionVerdict.NeedsCis,
            ChangeWorkflow.Check(Change(), ChangeRequestStatus.Submitted, ciCount: 0, Requester, Now));

    /// <summary>Cancelling is a way of saying no, and a malformed change need not be well-formed to drop.</summary>
    [Fact]
    public void Check_DraftToCancelled_WithNoCis_IsStillAllowed() =>
        Assert.Equal(
            ChangeTransitionVerdict.Allowed,
            ChangeWorkflow.Check(Change(), ChangeRequestStatus.Cancelled, ciCount: 0, Requester, Now));

    /// <summary>No shortcut past the decision: a draft is not approvable.</summary>
    [Fact]
    public void Check_DraftToApproved_IsNotPermitted() =>
        Assert.Equal(
            ChangeTransitionVerdict.NotPermitted,
            ChangeWorkflow.Check(Change(), ChangeRequestStatus.Approved, ciCount: 1, Approver, Now));

    [Fact]
    public void Check_SubmittedToApproved_BySomebodyElse_IsAllowed() =>
        Assert.Equal(
            ChangeTransitionVerdict.Allowed,
            ChangeWorkflow.Check(
                Change(ChangeRequestStatus.Submitted), ChangeRequestStatus.Approved, 1, Approver, Now));

    /// <summary>The one separation this workflow enforces, and the reason it is a rule about a record.</summary>
    [Fact]
    public void Check_SubmittedToApproved_ByItsOwnRequester_NeedsASecondPerson() =>
        Assert.Equal(
            ChangeTransitionVerdict.NeedsASecondPerson,
            ChangeWorkflow.Check(
                Change(ChangeRequestStatus.Submitted), ChangeRequestStatus.Approved, 1, Requester, Now));

    /// <summary>An unauthenticated caller is not "somebody else"; it falls on the restrictive side.</summary>
    [Fact]
    public void Check_SubmittedToApproved_WithNoActorId_NeedsASecondPerson() =>
        Assert.Equal(
            ChangeTransitionVerdict.NeedsASecondPerson,
            ChangeWorkflow.Check(
                Change(ChangeRequestStatus.Submitted), ChangeRequestStatus.Approved, 1, actorId: null, Now));

    /// <summary>
    /// A window wholly in the past would mute nothing while reporting the estate as maintained, so it is
    /// refused rather than opened.
    /// </summary>
    [Fact]
    public void Check_SubmittedToApproved_WithAWindowThatHasEnded_IsRefused()
    {
        var change = Change(ChangeRequestStatus.Submitted);
        change.PlannedStartAt = Now.AddHours(-4);
        change.PlannedEndAt = Now.AddHours(-2);

        Assert.Equal(
            ChangeTransitionVerdict.WindowHasPassed,
            ChangeWorkflow.Check(change, ChangeRequestStatus.Approved, 1, Approver, Now));
    }

    /// <summary>
    /// The start, though, may be in the past — a change approved as the work begins is the normal case,
    /// and refusing it because the slot opened ten minutes ago would make the feature unusable exactly
    /// when it is wanted.
    /// </summary>
    [Fact]
    public void Check_SubmittedToApproved_WithAWindowAlreadyUnderway_IsAllowed()
    {
        var change = Change(ChangeRequestStatus.Submitted);
        change.PlannedStartAt = Now.AddMinutes(-10);
        change.PlannedEndAt = Now.AddHours(2);

        Assert.Equal(
            ChangeTransitionVerdict.Allowed,
            ChangeWorkflow.Check(change, ChangeRequestStatus.Approved, 1, Approver, Now));
    }

    /// <summary>The backwards arrow that stops a stale submission being a dead end.</summary>
    [Fact]
    public void Check_SubmittedBackToDraft_IsAllowed() =>
        Assert.Equal(
            ChangeTransitionVerdict.Allowed,
            ChangeWorkflow.Check(
                Change(ChangeRequestStatus.Submitted), ChangeRequestStatus.Draft, 1, Requester, Now));

    [Fact]
    public void Check_ToTheStateItIsAlreadyIn_IsNoChange() =>
        Assert.Equal(
            ChangeTransitionVerdict.NoChange,
            ChangeWorkflow.Check(Change(), ChangeRequestStatus.Draft, 1, Requester, Now));

    [Fact]
    public void Check_OutOfApproved_IsNotPermitted() =>
        Assert.Equal(
            ChangeTransitionVerdict.NotPermitted,
            ChangeWorkflow.Check(
                Change(ChangeRequestStatus.Approved), ChangeRequestStatus.Cancelled, 1, Approver, Now));

    /// <summary>
    /// A refusal out of a terminal state cannot list what to do instead, so it says so rather than
    /// printing an empty list of options.
    /// </summary>
    [Fact]
    public void Explain_OutOfATerminalState_SaysTheChangeIsFinished()
    {
        var message = ChangeWorkflow.Explain(
            ChangeRequestStatus.Approved, ChangeRequestStatus.Cancelled, ChangeTransitionVerdict.NotPermitted);

        Assert.Contains("finished", message, StringComparison.Ordinal);
        Assert.Contains("Raise a new one", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ChangeTransitionVerdict.NoChange)]
    [InlineData(ChangeTransitionVerdict.NotPermitted)]
    [InlineData(ChangeTransitionVerdict.NeedsCis)]
    [InlineData(ChangeTransitionVerdict.WindowHasPassed)]
    [InlineData(ChangeTransitionVerdict.NeedsASecondPerson)]
    public void Explain_EveryRefusal_SaysSomething(ChangeTransitionVerdict verdict) =>
        Assert.NotEmpty(ChangeWorkflow.Explain(
            ChangeRequestStatus.Submitted, ChangeRequestStatus.Approved, verdict));

    private static ChangeRequest Change(ChangeRequestStatus status = ChangeRequestStatus.Draft) => new()
    {
        Id = Guid.CreateVersion7(),
        Title = "Firmware upgrade",
        Description = "The switch reboots twice.",
        Status = status,
        PlannedStartAt = Now.AddHours(1),
        PlannedEndAt = Now.AddHours(3),
        RequestedById = Requester,
        RequestedByName = "Requester",
        RequestedAt = Now.AddDays(-1),
        UpdatedAt = Now.AddDays(-1),
    };
}
