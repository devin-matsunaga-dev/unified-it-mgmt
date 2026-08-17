using System.Security.Claims;

namespace Platform.Dashboards;

/// <summary>
/// The unified dashboard: one read that returns this person's views, the layout to draw and every widget in
/// it (WP-5.5), plus the writes that keep several named arrangements.
/// </summary>
public interface IDashboardService
{
    Task<DashboardResponse> GetAsync(ClaimsPrincipal actor, CancellationToken cancellationToken);

    /// <summary>
    /// Creates a view and makes it the one on screen. An empty placement list is a blank slate, which is
    /// the point of being able to create one.
    /// </summary>
    Task<DashboardViewResult> CreateViewAsync(
        SaveDashboardViewRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken);

    /// <summary>Renames a view, replaces its cards, or both.</summary>
    Task<DashboardViewResult> SaveViewAsync(
        Guid viewId,
        SaveDashboardViewRequest request,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken);

    /// <summary>Switches which view is on screen.</summary>
    Task<DashboardViewResult> SelectViewAsync(
        Guid viewId,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken);

    /// <summary>
    /// Deletes a view. Deleting the last one leaves this person back on their role's default, which is the
    /// state they were in before they saved anything.
    /// </summary>
    Task<DashboardViewResult> DeleteViewAsync(
        Guid viewId,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken);
}
