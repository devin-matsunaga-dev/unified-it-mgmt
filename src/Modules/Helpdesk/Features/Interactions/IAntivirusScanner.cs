namespace Modules.Helpdesk.Features.Interactions;

public interface IAntivirusScanner
{
    Task<AntivirusScanResult> ScanAsync(Stream content, string fileName, CancellationToken cancellationToken);
}

public sealed record AntivirusScanResult(bool IsSafe, string? Reason = null);

public sealed class NoOpAntivirusScanner : IAntivirusScanner
{
    public Task<AntivirusScanResult> ScanAsync(
        Stream content, string fileName, CancellationToken cancellationToken) =>
        Task.FromResult(new AntivirusScanResult(true));
}
