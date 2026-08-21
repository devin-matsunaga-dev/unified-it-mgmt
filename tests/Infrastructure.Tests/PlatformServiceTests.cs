using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;

using Platform.Notifications;
using Platform.Scheduling;

namespace Infrastructure.Tests;

public sealed class PlatformServiceTests
{
    [Fact]
    public async Task SendAsync_NotificationTemplate_LogsRecipientAndTemplate()
    {
        var logger = new RecordingLogger<SmtpNotificationService>();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var service = new SmtpNotificationService(configuration, logger);
        var message = new NotificationMessage(
            "admin@example.test",
            new NotificationTemplate("TestTemplate", "Test subject", "Hello {{Name}}"),
            new { Name = "Admin" });

        await service.SendAsync(message);

        Assert.Contains("TestTemplate", logger.Message, StringComparison.Ordinal);
        Assert.Contains("admin@example.test", logger.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A relay we log in to must prove TLS first. Opportunistic STARTTLS would let a stripped
    /// advertisement put the relay password on the wire in the clear, which is the one outcome this
    /// choice exists to prevent.
    /// </summary>
    [Fact]
    public void TlsFor_WhenAuthenticating_RequiresStartTls()
    {
        Assert.Equal(SecureSocketOptions.StartTls, SmtpNotificationService.TlsFor(587, authenticating: true));
        Assert.Equal(SecureSocketOptions.StartTls, SmtpNotificationService.TlsFor(2525, authenticating: true));
    }

    /// <summary>465 carries TLS from the first byte and never offers STARTTLS to negotiate.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TlsFor_OnTheImplicitTlsPort_ConnectsOverSsl(bool authenticating)
    {
        Assert.Equal(
            SecureSocketOptions.SslOnConnect,
            SmtpNotificationService.TlsFor(SmtpNotificationService.ImplicitTlsPort, authenticating));
    }

    /// <summary>
    /// The local development sink: no credentials, no TLS, and it must keep working untouched — so an
    /// unauthenticated connection stays opportunistic rather than demanding what MailHog cannot offer.
    /// </summary>
    [Fact]
    public void TlsFor_WithoutCredentials_StaysOpportunistic()
    {
        Assert.Equal(
            SecureSocketOptions.StartTlsWhenAvailable,
            SmtpNotificationService.TlsFor(1025, authenticating: false));
    }

    [Fact]
    public async Task Execute_HeartbeatJob_LogsHeartbeat()
    {
        var logger = new RecordingLogger<PlatformHeartbeatJob>();
        var job = new PlatformHeartbeatJob(logger);

        await job.Execute(null!);

        Assert.Contains("Platform scheduler heartbeat", logger.Message, StringComparison.Ordinal);
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public string Message { get; private set; } = string.Empty;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) => Message = formatter(state, exception);
    }
}
