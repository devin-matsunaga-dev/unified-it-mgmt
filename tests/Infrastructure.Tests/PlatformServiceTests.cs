using Microsoft.Extensions.Logging;

using Platform.Notifications;
using Platform.Scheduling;

namespace Infrastructure.Tests;

public sealed class PlatformServiceTests
{
    [Fact]
    public async Task SendAsync_NotificationTemplate_LogsRecipientAndTemplate()
    {
        var logger = new RecordingLogger<LoggingNotificationService>();
        var service = new LoggingNotificationService(logger);
        var message = new NotificationMessage(
            "admin@example.test",
            new NotificationTemplate("TestTemplate", "Test subject", "Hello {{Name}}"),
            new { Name = "Admin" });

        await service.SendAsync(message);

        Assert.Contains("TestTemplate", logger.Message, StringComparison.Ordinal);
        Assert.Contains("admin@example.test", logger.Message, StringComparison.Ordinal);
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