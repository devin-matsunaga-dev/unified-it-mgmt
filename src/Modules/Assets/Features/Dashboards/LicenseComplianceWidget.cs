using System.Security.Claims;

using Modules.Assets.Features.Software;

using Platform.Actors;
using Platform.Dashboards;

namespace Modules.Assets.Features.Dashboards;

/// <summary>
/// Installed versus entitled, per product, summarised (WP-5.5) — and the products that are costing money or
/// risk, named.
/// <para>
/// It reads <see cref="ILicensingService.ReportAsync"/> rather than counting installs for itself, so the
/// card and the compliance report can never disagree about how many products are over-deployed. The states
/// themselves are <c>SoftwareComplianceCalculator</c>'s, which is where the two that are easy to get
/// backwards — unlicensed and merely over-deployed — are decided.
/// </para>
/// </summary>
public sealed class LicenseComplianceWidget(ILicensingService licensing) : IDashboardWidget
{
    public DashboardWidgetType Type => DashboardWidgetType.LicenseCompliance;

    public string Title => "Licence compliance";

    public bool IsVisibleTo(ClaimsPrincipal actor) => ActorRoles.IsAgent(actor);

    public async Task<DashboardWidgetData> LoadAsync(
        DashboardWidgetQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var report = await licensing.ReportAsync(new SoftwareComplianceRequest(null, null), cancellationToken);

        var unused = report.Rows.Count(row => row.State == SoftwareComplianceState.Unused);
        var compliant = report.ProductCount
            - report.OverDeployedCount - report.UnlicensedCount - unused;

        // Over-deployed first, worst shortfall at the top: it is the only one of the four states that is a
        // bill somebody will eventually be sent. Unlicensed products are listed too, but they are second —
        // every free browser and driver in the estate is unlicensed, which is why the compliance pass does
        // not notify about them either (WP-4.4).
        var rows = report.Rows
            .Where(row => row.State is SoftwareComplianceState.OverDeployed or SoftwareComplianceState.Unlicensed)
            .OrderBy(row => row.State == SoftwareComplianceState.OverDeployed ? 0 : 1)
            .ThenByDescending(row => row.Overage)
            .ThenByDescending(row => row.InstalledCiCount)
            .ThenBy(row => row.ProductName)
            .ToList();

        return new DashboardWidgetData(
            report.ProductCount == 0
                ? "No software product has been catalogued yet."
                : $"{report.ProductCount} catalogued product{(report.ProductCount == 1 ? "" : "s")}",
            report.OverDeployedCount,
            "Over-deployed",
            [
                new DashboardSegment("Over-deployed", report.OverDeployedCount, DashboardTone.Critical,
                    new DashboardLink(
                        DashboardLinkTarget.SoftwareCompliance, nameof(SoftwareComplianceState.OverDeployed))),
                new DashboardSegment("Unlicensed", report.UnlicensedCount, DashboardTone.Warning,
                    new DashboardLink(
                        DashboardLinkTarget.SoftwareCompliance, nameof(SoftwareComplianceState.Unlicensed))),
                new DashboardSegment("Unused entitlements", unused, DashboardTone.Info,
                    new DashboardLink(
                        DashboardLinkTarget.SoftwareCompliance, nameof(SoftwareComplianceState.Unused))),
                new DashboardSegment("Compliant", compliant, DashboardTone.Ok,
                    new DashboardLink(
                        DashboardLinkTarget.SoftwareCompliance, nameof(SoftwareComplianceState.Compliant))),
            ],
            [
                .. rows.Take(query.RowLimit).Select(row => new DashboardRow(
                    $"{row.Publisher} {row.ProductName}".Trim(),
                    row.State == SoftwareComplianceState.OverDeployed
                        ? $"{row.InstalledCiCount} installed · {row.Entitled} entitled"
                        : $"{row.InstalledCiCount} installed · nothing bought",
                    row.State == SoftwareComplianceState.OverDeployed
                        ? $"{row.Overage} over"
                        : "Unlicensed",
                    row.State == SoftwareComplianceState.OverDeployed
                        ? DashboardTone.Critical
                        : DashboardTone.Warning,
                    new DashboardLink(
                        DashboardLinkTarget.SoftwareCompliance, row.State.ToString()))),
            ],
            rows.Count,
            new DashboardLink(DashboardLinkTarget.SoftwareCompliance),
            report.OverDeployedCount > 0 ? DashboardTone.Critical
                : report.UnlicensedCount > 0 ? DashboardTone.Warning
                : DashboardTone.Ok);
    }
}
