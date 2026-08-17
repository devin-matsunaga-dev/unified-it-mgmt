using System.Security.Claims;

using Platform.Dashboards;

namespace Infrastructure.Tests;

/// <summary>
/// The layout rules on their own, with no database and no widgets (WP-5.5): which default a role opens on,
/// and what happens to a saved layout when the platform changes underneath it.
/// <para>
/// These are the rules a saved layout has to survive: a widget added in a later release, a widget removed,
/// and a change of role. All three happen to a stored row that nobody will ever edit by hand.
/// </para>
/// </summary>
public sealed class DashboardDefaultsTests
{
    private static readonly DashboardWidgetType[] AllWidgets = Enum.GetValues<DashboardWidgetType>();

    /// <summary>The WP's own verification step: a manager opens on the executive layout.</summary>
    [Fact]
    public void PresetFor_AManager_IsTheExecutiveLayout()
    {
        Assert.Equal(DashboardPreset.Executive, DashboardDefaults.PresetFor(Principal("Manager")));
        Assert.Equal(
            DashboardWidgetType.SlaHealth,
            DashboardDefaults.For(DashboardPreset.Executive)[0].Type);
    }

    [Theory]
    [InlineData("Admin")]
    [InlineData("Technician")]
    public void PresetFor_AnOperatorWhoIsNotAManager_IsTheOperationsLayout(string role)
    {
        Assert.Equal(DashboardPreset.Operations, DashboardDefaults.PresetFor(Principal(role)));
        Assert.Equal(
            DashboardWidgetType.NetworkStatus,
            DashboardDefaults.For(DashboardPreset.Operations)[0].Type);
    }

    /// <summary>
    /// An admin who is also a manager gets the executive default. The more specific claim about what
    /// somebody is there to do wins — and this decides a layout, never a permission.
    /// </summary>
    [Fact]
    public void PresetFor_AnAdminWhoIsAlsoAManager_IsTheExecutiveLayout()
    {
        Assert.Equal(DashboardPreset.Executive, DashboardDefaults.PresetFor(Principal("Admin", "Manager")));
    }

    /// <summary>Both defaults place every widget, or the ones they miss would be appended in enum order.</summary>
    [Fact]
    public void EveryDefaultLayout_PlacesEveryWidgetExactlyOnce()
    {
        foreach (var preset in Enum.GetValues<DashboardPreset>())
        {
            var placed = DashboardDefaults.For(preset).Select(placement => placement.Type).ToList();
            Assert.Equal(AllWidgets.Length, placed.Count);
            Assert.Equal(AllWidgets.Length, placed.Distinct().Count());
        }
    }

    [Fact]
    public void Compose_WithNothingSaved_IsTheDefaultForTheRole()
    {
        var composed = DashboardDefaults.Compose(null, DashboardPreset.Executive, AllWidgets);

        Assert.Equal(DashboardDefaults.Executive, composed);
    }

    [Fact]
    public void Compose_WithASavedLayout_KeepsItsOrderAndItsWidths()
    {
        IReadOnlyList<DashboardPlacement> saved =
        [
            new(DashboardWidgetType.LicenseCompliance, DashboardWidgetWidth.Full),
            new(DashboardWidgetType.SlaHealth, DashboardWidgetWidth.Third),
            new(DashboardWidgetType.NetworkStatus, DashboardWidgetWidth.Third),
            new(DashboardWidgetType.OpenByPriority, DashboardWidgetWidth.Third),
            new(DashboardWidgetType.RecentRootCauses, DashboardWidgetWidth.Full),
        ];

        var composed = DashboardDefaults.Compose(saved, DashboardPreset.Operations, AllWidgets);

        Assert.Equal(saved, composed);
    }

    /// <summary>
    /// A view holds exactly what its owner put in it. Nothing is appended, which is the rule that changed
    /// when views arrived: the first cut appended every unplaced widget so that a later release could not
    /// be invisible, and that makes a blank slate impossible and silently re-adds every card anybody
    /// deliberately removed. Discoverability moved to the card menu instead.
    /// </summary>
    [Fact]
    public void Compose_WithASavedViewNamingOneWidget_DrawsThatWidgetAndNothingElse()
    {
        IReadOnlyList<DashboardPlacement> saved =
        [
            new(DashboardWidgetType.SlaHealth, DashboardWidgetWidth.Full),
        ];

        var composed = DashboardDefaults.Compose(saved, DashboardPreset.Executive, AllWidgets);

        Assert.Equal([DashboardWidgetType.SlaHealth], composed.Select(placement => placement.Type));
        Assert.Equal(DashboardWidgetWidth.Full, composed[0].Width);
    }

    /// <summary>
    /// The blank slate, which is the whole point of being able to create a view: an empty one stays empty
    /// rather than refilling itself with the role default.
    /// </summary>
    [Fact]
    public void Compose_WithAnEmptySavedView_DrawsNothing()
    {
        Assert.Empty(DashboardDefaults.Compose([], DashboardPreset.Operations, AllWidgets));
    }

    /// <summary>Nothing saved at all is still the role default — that is a different state from an empty view.</summary>
    [Fact]
    public void Compose_WithNoSavedViewAtAll_IsTheRoleDefault()
    {
        Assert.Equal(
            DashboardDefaults.Operations,
            DashboardDefaults.Compose(null, DashboardPreset.Operations, AllWidgets));
    }

    /// <summary>
    /// A layout outlives a change of role, so a placement the actor can no longer see is dropped rather
    /// than leaving a hole — or, worse, drawing a card they may not read.
    /// </summary>
    [Fact]
    public void Compose_WithAPlacementTheActorCannotSee_DropsIt()
    {
        IReadOnlyList<DashboardPlacement> saved =
        [
            new(DashboardWidgetType.SlaHealth, DashboardWidgetWidth.Half),
            new(DashboardWidgetType.LicenseCompliance, DashboardWidgetWidth.Half),
        ];

        var composed = DashboardDefaults.Compose(
            saved, DashboardPreset.Executive, [DashboardWidgetType.SlaHealth]);

        Assert.Equal([DashboardWidgetType.SlaHealth], composed.Select(placement => placement.Type));
    }

    /// <summary>
    /// Duplicates are refused at the edge, so a stored one predates that check. It keeps its first place
    /// rather than being drawn twice — a card cannot be in two places at once.
    /// </summary>
    [Fact]
    public void Compose_WithAWidgetPlacedTwice_KeepsTheFirstPlaceOnly()
    {
        IReadOnlyList<DashboardPlacement> saved =
        [
            new(DashboardWidgetType.SlaHealth, DashboardWidgetWidth.Third),
            new(DashboardWidgetType.NetworkStatus, DashboardWidgetWidth.Half),
            new(DashboardWidgetType.SlaHealth, DashboardWidgetWidth.Full),
        ];

        var composed = DashboardDefaults.Compose(
            saved,
            DashboardPreset.Operations,
            [DashboardWidgetType.SlaHealth, DashboardWidgetType.NetworkStatus]);

        Assert.Equal(
            [DashboardWidgetType.SlaHealth, DashboardWidgetType.NetworkStatus],
            composed.Select(placement => placement.Type));
        Assert.Equal(DashboardWidgetWidth.Third, composed[0].Width);
    }

    /// <summary>
    /// The failure path: an actor who may see nothing gets an empty layout rather than the role default
    /// drawn over widgets that would all refuse to load. An end user reaching this endpoint is exactly that
    /// case.
    /// </summary>
    [Fact]
    public void Compose_WhenNoWidgetIsVisible_IsEmptyRatherThanTheDefault()
    {
        Assert.Empty(DashboardDefaults.Compose(null, DashboardPreset.Operations, []));
        Assert.Empty(DashboardDefaults.Compose(DashboardDefaults.Executive, DashboardPreset.Executive, []));
        Assert.Empty(DashboardDefaults.Compose([], DashboardPreset.Executive, []));
    }

    /// <summary>
    /// The shape a card is drawn as travels with the placement, so a composed layout keeps it. It is a
    /// presentation choice and the server has no opinion about it beyond storing it and validating it.
    /// </summary>
    [Fact]
    public void Compose_KeepsTheShapeEachCardIsDrawnAs()
    {
        IReadOnlyList<DashboardPlacement> saved =
        [
            new(DashboardWidgetType.OpenByPriority, DashboardWidgetWidth.Third, DashboardDisplay.Donut),
            new(DashboardWidgetType.NetworkStatus, DashboardWidgetWidth.Third, DashboardDisplay.Bar),
        ];

        var composed = DashboardDefaults.Compose(saved, DashboardPreset.Operations, AllWidgets);

        Assert.Equal([DashboardDisplay.Donut, DashboardDisplay.Bar],
            composed.Select(placement => placement.Display));
    }

    /// <summary>A placement that says nothing about its shape is a card, which is what every default is.</summary>
    [Fact]
    public void APlacementWithNoShapeGiven_IsACard()
    {
        Assert.Equal(
            DashboardDisplay.Card,
            new DashboardPlacement(DashboardWidgetType.SlaHealth, DashboardWidgetWidth.Half).Display);
    }

    private static ClaimsPrincipal Principal(params string[] roles) =>
        new(new ClaimsIdentity(
            [.. roles.Select(role => new Claim(ClaimTypes.Role, role)), new Claim("sub", "dashboard-tests")],
            "Test"));
}
