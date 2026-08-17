using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using MassTransit.EntityFrameworkCoreIntegration;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Modules.Assets.Data;

using Platform.Data;

namespace Infrastructure.Tests;

/// <summary>
/// Change requests end to end (WP-5.8): what a change is, who may agree to it, and what leaves the
/// module when somebody does.
/// <para>
/// The suite shares its database with forty other classes, so nothing here counts anything estate-wide.
/// Every test works on CIs it created itself and asserts on those.
/// </para>
/// <para>
/// The approval's other half — the maintenance window, and the alerts it silences — is proved in
/// <c>AlertEngineIntegrationTests</c>, where the alert engine and a real Redis already are.
/// </para>
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class ChangeApiIntegrationTests(InfrastructureFixture infrastructure, ChangeHostFixture host)
    : IClassFixture<ChangeHostFixture>, IAsyncLifetime
{
    private const string Requester = ChangeHostFixture.ChangeAuthenticationHandler.DefaultActorId;
    private const string Approver = ChangeHostFixture.ChangeAuthenticationHandler.OtherActorId;

    public async Task InitializeAsync()
    {
        ArgumentNullException.ThrowIfNull(host);
        await host.EnsureInitialisedAsync(infrastructure);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task AssetsMigrations_CurrentModel_HasNoPendingChanges()
    {
        await using var scope = host.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AssetsDbContext>();

        Assert.False(context.Database.HasPendingModelChanges());
    }

    // ---- what a change is ----

    [Fact]
    public async Task CreateChange_WithCis_OpensAsADraftNumberedChg()
    {
        var ci = await CreateCiAsync("Access switch");

        var change = await CreateChangeAsync([ci]);

        Assert.StartsWith("CHG-", change.Number, StringComparison.Ordinal);
        Assert.Equal("Draft", change.Status);
        Assert.Equal(1, change.CiCount);
        Assert.Equal(0, change.DependentCount);
        Assert.Equal(Requester, change.RequestedById);
        Assert.Equal(["Submitted", "Cancelled"], change.NextStatuses);

        var read = await host.GetAsync<ChangeDto>($"/api/changes/{change.Id}");
        var scope = Assert.Single(read.Cis!);
        Assert.Equal(ci, scope.CiId);
        Assert.False(scope.IsDependent);
    }

    [Fact]
    public async Task CreateChange_NamingACiThatDoesNotExist_IsRefusedWithAFieldError()
    {
        var missing = Guid.CreateVersion7();

        using var response = await host.SendAsync(HttpMethod.Post, "/api/changes", new
        {
            title = "Upgrade something imaginary",
            description = "It does not exist.",
            plannedStartAt = DateTimeOffset.UtcNow.AddHours(1),
            plannedEndAt = DateTimeOffset.UtcNow.AddHours(2),
            ciIds = new[] { missing },
        });
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains(missing.ToString(), body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateChange_EndingBeforeItStarts_IsRefusedWithAFieldError()
    {
        var ci = await CreateCiAsync("Backwards switch");

        using var response = await host.SendAsync(HttpMethod.Post, "/api/changes", new
        {
            title = "Time travel",
            description = "Ends before it starts.",
            plannedStartAt = DateTimeOffset.UtcNow.AddHours(3),
            plannedEndAt = DateTimeOffset.UtcNow.AddHours(1),
            ciIds = new[] { ci },
        });
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("PlannedEndAt", body, StringComparison.Ordinal);
    }

    // ---- editing ----

    [Fact]
    public async Task UpdateChange_WhileItIsADraft_ReplacesTheWholeCiList()
    {
        var first = await CreateCiAsync("First switch");
        var second = await CreateCiAsync("Second switch");
        var change = await CreateChangeAsync([first]);

        var updated = await UpdateAsync(change.Id, [second]);

        Assert.Equal(second, Assert.Single(updated.Cis!).CiId);
    }

    /// <summary>
    /// The failure path that protects a reviewer: what somebody is deciding about must not move
    /// underneath them.
    /// </summary>
    [Fact]
    public async Task UpdateChange_OnceSubmitted_IsRefusedWithAConflict()
    {
        var ci = await CreateCiAsync("Submitted switch");
        var change = await CreateChangeAsync([ci]);
        await TransitionAsync(change.Id, "Submitted");

        using var response = await host.SendAsync(HttpMethod.Put, $"/api/changes/{change.Id}", new
        {
            title = "Edited after submission",
            description = "Should not be allowed.",
            plannedStartAt = DateTimeOffset.UtcNow.AddHours(1),
            plannedEndAt = DateTimeOffset.UtcNow.AddHours(2),
            ciIds = new[] { ci },
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains(
            "only be edited while it is a draft",
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// And the way out of that: withdraw to draft, edit, resubmit. Without this arrow a change whose
    /// window slipped while it waited would be one nobody can approve and nobody can fix.
    /// </summary>
    [Fact]
    public async Task TransitionChange_SubmittedBackToDraft_MakesItEditableAgain()
    {
        var ci = await CreateCiAsync("Withdrawn switch");
        var change = await CreateChangeAsync([ci]);
        await TransitionAsync(change.Id, "Submitted");

        var withdrawn = await TransitionAsync(change.Id, "Draft");
        Assert.Equal("Draft", withdrawn.Status);

        var edited = await UpdateAsync(change.Id, [ci], title: "Rescheduled firmware upgrade");
        Assert.Equal("Rescheduled firmware upgrade", edited.Title);
    }

    // ---- the decision ----

    [Fact]
    public async Task TransitionChange_ApprovedBySomebodyElse_PublishesTheApprovalThroughTheOutbox()
    {
        var ci = await CreateCiAsync("Approved switch");
        var change = await CreateChangeAsync([ci]);
        await TransitionAsync(change.Id, "Submitted");

        var approved = await TransitionAsync(change.Id, "Approved", actorId: Approver, note: "Agreed at CAB.");

        Assert.Equal("Approved", approved.Status);
        Assert.Equal(Approver, approved.DecidedById);
        Assert.Equal("Agreed at CAB.", approved.DecisionNote);
        Assert.Empty(approved.NextStatuses);

        var published = Assert.Single(await PublishedApprovalsAsync(change.Id));
        Assert.Equal(change.Number, published.Number);
        Assert.Equal(ci, Assert.Single(published.CiIds));
    }

    /// <summary>
    /// The separation of duties, which is the one rule this feature enforces about people rather than
    /// about records — and it is enforced below the UI, not by hiding a button.
    /// </summary>
    [Fact]
    public async Task TransitionChange_ApprovedByItsOwnRequester_IsRefusedWithForbidden()
    {
        var ci = await CreateCiAsync("Self-approved switch");
        var change = await CreateChangeAsync([ci]);
        await TransitionAsync(change.Id, "Submitted");

        using var response = await host.SendAsync(
            HttpMethod.Post, $"/api/changes/{change.Id}/transitions", new { targetStatus = "Approved" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains(
            "somebody other than the person who raised it",
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);

        // And it really did not move, so nothing was published either.
        Assert.Equal("Submitted", (await host.GetAsync<ChangeDto>($"/api/changes/{change.Id}")).Status);
        Assert.Empty(await PublishedApprovalsAsync(change.Id));
    }

    /// <summary>
    /// A change whose slot has been and gone would open a maintenance window that mutes nothing while
    /// reporting the estate as maintained.
    /// </summary>
    [Fact]
    public async Task TransitionChange_ApprovedAfterItsWindowEnded_IsRefusedWithAFieldError()
    {
        var ci = await CreateCiAsync("Stale switch");
        var change = await CreateChangeAsync(
            [ci],
            plannedStartAt: DateTimeOffset.UtcNow.AddHours(-4),
            plannedEndAt: DateTimeOffset.UtcNow.AddHours(-2));
        await TransitionAsync(change.Id, "Submitted");

        using var response = await host.SendAsync(
            HttpMethod.Post, $"/api/changes/{change.Id}/transitions",
            new { targetStatus = "Approved" }, actorId: Approver);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("PlannedEndAt", body, StringComparison.Ordinal);
        Assert.Empty(await PublishedApprovalsAsync(change.Id));
    }

    [Fact]
    public async Task TransitionChange_OutOfAnApprovedChange_IsRefusedWithAConflict()
    {
        var ci = await CreateCiAsync("Terminal switch");
        var change = await CreateChangeAsync([ci]);
        await TransitionAsync(change.Id, "Submitted");
        await TransitionAsync(change.Id, "Approved", actorId: Approver);

        using var response = await host.SendAsync(
            HttpMethod.Post, $"/api/changes/{change.Id}/transitions",
            new { targetStatus = "Cancelled" }, actorId: Approver);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task TransitionChange_SubmittedWithNoCis_IsRefused()
    {
        var ci = await CreateCiAsync("Emptied switch");
        var change = await CreateChangeAsync([ci]);
        await UpdateAsync(change.Id, []);

        using var response = await host.SendAsync(
            HttpMethod.Post, $"/api/changes/{change.Id}/transitions", new { targetStatus = "Submitted" });

        // 400 rather than 409: an empty CI list is a malformed change, not an illegal move.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(
            "Name at least one configuration item",
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    // ---- "+ dependents optional" ----

    /// <summary>
    /// The WP's optional half. Rebooting the switch disturbs what hangs off it, and the approval carries
    /// those CIs so the window covers them too.
    /// </summary>
    [Fact]
    public async Task TransitionChange_ApprovedWithDependents_AddsWhatDependsOnItAndCarriesThemOnTheEvent()
    {
        var switchCi = await CreateCiAsync("Uplink switch");
        var serverCi = await CreateCiAsync("Server behind it");
        await RelateAsync(serverCi, switchCi);

        var change = await CreateChangeAsync([switchCi], includeDependents: true);
        await TransitionAsync(change.Id, "Submitted");
        var approved = await TransitionAsync(change.Id, "Approved", actorId: Approver);

        Assert.Equal(2, approved.CiCount);
        Assert.Equal(1, approved.DependentCount);
        var dependent = Assert.Single(approved.Cis!, scope => scope.IsDependent);
        Assert.Equal(serverCi, dependent.CiId);

        var published = Assert.Single(await PublishedApprovalsAsync(change.Id));
        Assert.Equal(new[] { switchCi, serverCi }.Order(), published.CiIds.Order());
    }

    /// <summary>
    /// Without the flag, the graph is not walked at all — the operator asked to disturb one thing, and a
    /// window that quietly covered its neighbours would silence alerts nobody agreed to silence.
    /// </summary>
    [Fact]
    public async Task TransitionChange_ApprovedWithoutDependents_CoversOnlyWhatWasNamed()
    {
        var switchCi = await CreateCiAsync("Lone switch");
        var serverCi = await CreateCiAsync("Server nobody mentioned");
        await RelateAsync(serverCi, switchCi);

        var change = await CreateChangeAsync([switchCi], includeDependents: false);
        await TransitionAsync(change.Id, "Submitted");
        var approved = await TransitionAsync(change.Id, "Approved", actorId: Approver);

        Assert.Equal(1, approved.CiCount);
        Assert.Equal(0, approved.DependentCount);
        Assert.Equal(switchCi, Assert.Single((await PublishedApprovalsAsync(change.Id))[0].CiIds));
    }

    // ---- reading ----

    /// <summary>The calendar's query: a change is in a month when its window overlaps it at all.</summary>
    [Fact]
    public async Task ListChanges_ByDateRange_ReturnsChangesThatOverlapTheRangeRatherThanSitInside()
    {
        var ci = await CreateCiAsync("Straddling switch");
        var start = DateTimeOffset.UtcNow.AddDays(30);
        var change = await CreateChangeAsync(
            [ci], plannedStartAt: start, plannedEndAt: start.AddDays(2));

        // A range that begins after the change starts and ends before it ends: it overlaps, so it counts.
        var page = await host.GetAsync<ChangePageDto>(
            $"/api/changes?from={Uri.EscapeDataString(start.AddHours(6).ToString("O"))}"
            + $"&to={Uri.EscapeDataString(start.AddHours(12).ToString("O"))}&pageSize=200");

        Assert.Contains(page.Items, item => item.Id == change.Id);
    }

    [Fact]
    public async Task ListChanges_ByCi_ReturnsOnlyChangesThatNameIt()
    {
        var mine = await CreateCiAsync("Filtered switch");
        var other = await CreateCiAsync("Unrelated switch");
        var change = await CreateChangeAsync([mine]);
        await CreateChangeAsync([other]);

        var page = await host.GetAsync<ChangePageDto>($"/api/changes?ciId={mine}&pageSize=200");

        Assert.Equal(change.Id, Assert.Single(page.Items).Id);
    }

    /// <summary>
    /// The three-clause closed-enum guard (WP-5.6): <c>TryParse</c> accepts "3" and 3 is a defined
    /// member, so without the name comparison this would silently filter by whatever sits at that ordinal.
    /// </summary>
    [Fact]
    public async Task ListChanges_WithANumericStatus_IsRefusedRatherThanSilentlyFiltered()
    {
        using var request = host.Request(HttpMethod.Get, "/api/changes?status=3");
        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- the guard the Restrict foreign key needs ----

    /// <summary>
    /// A CI listed on a change is half of an agreement two people made about it. The foreign key would
    /// refuse the delete anyway; this proves it comes back as a 409 that says what is in the way rather
    /// than as a database error.
    /// </summary>
    [Fact]
    public async Task DeleteCi_WhileAChangeStillNamesIt_IsRefusedWithAConflict()
    {
        var ci = await CreateCiAsync("Spoken-for switch");
        await CreateChangeAsync([ci]);

        using var response = await host.SendAsync(HttpMethod.Delete, $"/api/cis/{ci}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    // ---- helpers ----

    private async Task<Guid> CreateCiAsync(string name)
    {
        using var response = await host.SendAsync(HttpMethod.Post, "/api/cis", new
        {
            type = "NetworkDevice",
            name = $"{name} {Guid.NewGuid():N}"[..40],
            attributes = new Dictionary<string, string>
            {
                ["managementIp"] = "10.55.0.1",
                ["vendor"] = "Acme",
                ["portCount"] = "48",
            },
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var ci = await response.Content.ReadFromJsonAsync<CiIdDto>();
        return ci!.Id;
    }

    /// <summary>"<paramref name="sourceCiId"/> needs <paramref name="targetCiId"/>" — WP-2.3's direction.</summary>
    private async Task RelateAsync(Guid sourceCiId, Guid targetCiId)
    {
        using var response = await host.SendAsync(
            HttpMethod.Post, $"/api/cis/{sourceCiId}/relationships", new { targetCiId, type = "DependsOn" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private async Task<ChangeDto> CreateChangeAsync(
        IReadOnlyList<Guid> ciIds,
        bool includeDependents = false,
        DateTimeOffset? plannedStartAt = null,
        DateTimeOffset? plannedEndAt = null)
    {
        using var response = await host.SendAsync(HttpMethod.Post, "/api/changes", new
        {
            title = "Firmware upgrade",
            description = "The switch reboots twice during the upgrade.",
            plannedStartAt = plannedStartAt ?? DateTimeOffset.UtcNow.AddHours(1),
            plannedEndAt = plannedEndAt ?? DateTimeOffset.UtcNow.AddHours(3),
            ciIds,
            includeDependents,
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ChangeDto>())!;
    }

    private async Task<ChangeDto> UpdateAsync(
        Guid id,
        IReadOnlyList<Guid> ciIds,
        string title = "Firmware upgrade")
    {
        using var response = await host.SendAsync(HttpMethod.Put, $"/api/changes/{id}", new
        {
            title,
            description = "The switch reboots twice during the upgrade.",
            plannedStartAt = DateTimeOffset.UtcNow.AddHours(1),
            plannedEndAt = DateTimeOffset.UtcNow.AddHours(3),
            ciIds,
            includeDependents = false,
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ChangeDto>())!;
    }

    private async Task<ChangeDto> TransitionAsync(
        Guid id,
        string targetStatus,
        string actorId = Requester,
        string? note = null)
    {
        using var response = await host.SendAsync(
            HttpMethod.Post, $"/api/changes/{id}/transitions", new { targetStatus, note }, actorId: actorId);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ChangeDto>())!;
    }

    /// <summary>
    /// What actually reached the transactional outbox for this change. Asserting on the outbox rather
    /// than on a test consumer is the point: ARCHITECTURE §4 requires every publish to go through it, so
    /// a message that is not here was not published the way this platform publishes things.
    /// </summary>
    private async Task<IReadOnlyList<ApprovalDto>> PublishedApprovalsAsync(Guid changeId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var bodies = await context.Set<OutboxMessage>()
            .Where(message => message.MessageType!.Contains("ChangeRequestApproved")
                && message.Body.Contains(changeId.ToString()))
            .OrderBy(message => message.SequenceNumber)
            .Select(message => message.Body)
            .ToListAsync();

        return [.. bodies.Select(Deserialize<ApprovalDto>)];
    }

    /// <summary>The envelope is MassTransit's, so the event itself is under <c>message</c>.</summary>
    private static T Deserialize<T>(string body)
    {
        using var document = JsonDocument.Parse(body);
        return JsonSerializer.Deserialize<T>(
            document.RootElement.GetProperty("message").GetRawText(),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    private sealed record CiIdDto(Guid Id);

    private sealed record ChangeCiDto(Guid CiId, string? Name, bool IsDependent);

    private sealed record ChangeDto(
        Guid Id,
        string Number,
        string Title,
        string Status,
        int CiCount,
        int DependentCount,
        string RequestedById,
        string? DecidedById,
        string? DecisionNote,
        IReadOnlyList<string> NextStatuses,
        IReadOnlyList<ChangeCiDto>? Cis);

    private sealed record ChangePageDto(IReadOnlyList<ChangeDto> Items, int Total);

    private sealed record ApprovalDto(
        Guid ChangeRequestId,
        string Number,
        string Title,
        DateTimeOffset StartsAt,
        DateTimeOffset EndsAt,
        IReadOnlyList<Guid> CiIds);
}
