using Modules.Monitoring.Data;
using Modules.Monitoring.Features.Runbooks;

namespace Infrastructure.Tests;

/// <summary>
/// The allowlist and the parameter schema, with no database anywhere near them. These are the two
/// pure decisions WP-5.6 rests on: what may run, and what it may be told.
/// </summary>
public sealed class RunbookCatalogTests
{
    [Fact]
    public void Catalogue_HoldsTheRunbookThisPlatformShips()
    {
        var definition = Assert.Single(RunbookCatalog.All);

        Assert.Equal(RunbookCatalog.RestartService, definition.Key);
        Assert.Equal("service", Assert.Single(definition.Parameters).Name);
    }

    /// <summary>
    /// The property the whole package is built on, written as a demonstration rather than an
    /// assertion about one string: whatever a caller asks for, if the catalogue does not name it the
    /// answer is null and every caller treats null as "refuse".
    /// </summary>
    [Theory]
    [InlineData("delete-everything")]
    [InlineData("restart-service; rm -rf /")]
    [InlineData("../../bin/sh")]
    [InlineData("")]
    [InlineData("   ")]
    public void Find_AKeyTheCatalogueDoesNotName_IsNull(string key)
    {
        Assert.Null(RunbookCatalog.Find(key));
        Assert.False(RunbookCatalog.Contains(key));
    }

    /// <summary>
    /// Case-insensitive lookup, canonical storage. Without it two registrations could differ only by
    /// case, and the unique index — which is ordinal — would let both exist with two rate limits over
    /// the same action.
    /// </summary>
    [Fact]
    public void Canonicalise_AKeyInADifferentCase_ReturnsTheCatalogueSpelling()
    {
        Assert.Equal(RunbookCatalog.RestartService, RunbookCatalog.Canonicalise("Restart-Service"));
        Assert.Equal(RunbookCatalog.RestartService, RunbookCatalog.Canonicalise(" restart-service "));
    }
}

public sealed class RunbookParameterRulesTests
{
    private static readonly RunbookDefinition RestartService =
        RunbookCatalog.Find(RunbookCatalog.RestartService)!;

    [Fact]
    public void Bind_AValidServiceName_ReturnsItTrimmed()
    {
        var binding = RunbookParameterRules.Bind(RestartService, Parameters(("service", "  nginx  ")));

        Assert.True(binding.IsValid);
        Assert.Equal("nginx", binding.Values!["service"]);
    }

    [Theory]
    [InlineData("nginx.service")]
    [InlineData("postgresql@16-main")]
    [InlineData("my_app-1")]
    public void Bind_AServiceNameInEveryShapeSystemdAllows_IsAccepted(string service)
    {
        Assert.True(RunbookParameterRules.Bind(RestartService, Parameters(("service", service))).IsValid);
    }

    /// <summary>
    /// The failure path this package exists for. Every one of these is a way somebody might try to
    /// make one allowlisted runbook run something else, and each is refused rather than escaped —
    /// escaping is how a rejected input becomes an accepted one.
    /// </summary>
    [Theory]
    [InlineData("nginx; rm -rf /")]
    [InlineData("nginx && reboot")]
    [InlineData("nginx | tee /etc/shadow")]
    [InlineData("$(reboot)")]
    [InlineData("`reboot`")]
    [InlineData("../../etc/passwd")]
    [InlineData("nginx\nreboot")]
    [InlineData("nginx reboot")]
    [InlineData("-rf")]
    public void Bind_AServiceNameCarryingAnythingButAName_IsRefused(string service)
    {
        var binding = RunbookParameterRules.Bind(RestartService, Parameters(("service", service)));

        Assert.False(binding.IsValid);
        Assert.Contains("parameters.service", binding.Errors!.Keys);
    }

    /// <summary>
    /// The refusal does not quote the pattern back. It is a security control, and echoing it turns a
    /// "no" into instructions for writing something that gets through.
    /// </summary>
    [Fact]
    public void Bind_ARefusal_DoesNotRevealThePattern()
    {
        var binding = RunbookParameterRules.Bind(RestartService, Parameters(("service", "nginx; id")));

        var message = Assert.Single(binding.Errors!["parameters.service"]);
        Assert.DoesNotContain("^[A-Za-z0-9]", message, StringComparison.Ordinal);
        Assert.Contains("nginx", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Bind_AParameterTheRunbookDoesNotTake_IsRefusedRatherThanDropped()
    {
        var binding = RunbookParameterRules.Bind(
            RestartService, Parameters(("service", "nginx"), ("command", "reboot")));

        Assert.False(binding.IsValid);
        Assert.Contains("parameters.command", binding.Errors!.Keys);
    }

    [Fact]
    public void Bind_WithNoParametersAtAll_ReportsTheRequiredOne()
    {
        var binding = RunbookParameterRules.Bind(RestartService, null);

        Assert.False(binding.IsValid);
        Assert.Contains("parameters.service", binding.Errors!.Keys);
    }

    [Fact]
    public void Bind_AValueLongerThanTheSchemaAllows_IsRefused()
    {
        var binding = RunbookParameterRules.Bind(
            RestartService, Parameters(("service", new string('a', 65))));

        Assert.False(binding.IsValid);
    }

    /// <summary>
    /// A misspelt name is two faults at once — an unknown parameter and a missing required one — and
    /// both are reported, because being told only one of them makes the other invisible.
    /// </summary>
    [Fact]
    public void Bind_AMisspeltParameterName_ReportsBothFaults()
    {
        var binding = RunbookParameterRules.Bind(RestartService, Parameters(("Service", "nginx")));

        Assert.False(binding.IsValid);
        Assert.Equal(
            ["parameters.Service", "parameters.service"],
            binding.Errors!.Keys.Order(StringComparer.Ordinal));
    }

    private static Dictionary<string, string> Parameters(params (string Key, string Value)[] values) =>
        values.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
}

/// <summary>
/// The per-runbook bound. Pure, because the count it judges is taken by the caller — which is what
/// lets the whole matrix be asserted without a database, and is also why the count is exact rather
/// than cached (see <see cref="RunbookRateLimit"/>).
/// </summary>
public sealed class RunbookRateLimitTests
{
    [Fact]
    public void Evaluate_WithRoomInTheWindow_Allows()
    {
        var decision = RunbookRateLimit.Evaluate(
            Runbook(allowance: 5), new RunbookOptions(), recentExecutions: 4, isAutomatic: true);

        Assert.True(decision.IsAllowed);
    }

    [Fact]
    public void Evaluate_AtTheAllowance_RefusesAndSaysWhy()
    {
        var decision = RunbookRateLimit.Evaluate(
            Runbook(allowance: 5), new RunbookOptions(), recentExecutions: 5, isAutomatic: true);

        Assert.Equal(RunbookVerdict.RateLimited, decision.Verdict);
        Assert.Contains("its limit is 5", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_WithThePlatformSwitchOff_RefusesEvenAManualRun()
    {
        var decision = RunbookRateLimit.Evaluate(
            Runbook(), new RunbookOptions { Enabled = false }, recentExecutions: 0, isAutomatic: false);

        Assert.Equal(RunbookVerdict.Disabled, decision.Verdict);
    }

    /// <summary>
    /// The two switches do different things, and this is the difference: with automatic triggers
    /// stood down an operator can still run one deliberately, which is the state somebody wants while
    /// they work out why the automation misfired.
    /// </summary>
    [Fact]
    public void Evaluate_WithAutomaticTriggersOff_RefusesTheTriggerAndAllowsTheOperator()
    {
        var options = new RunbookOptions { AutomaticTriggersEnabled = false };

        Assert.Equal(
            RunbookVerdict.Disabled,
            RunbookRateLimit.Evaluate(Runbook(), options, 0, isAutomatic: true).Verdict);
        Assert.True(RunbookRateLimit.Evaluate(Runbook(), options, 0, isAutomatic: false).IsAllowed);
    }

    [Fact]
    public void Evaluate_ADisabledRunbook_RefusesBothPaths()
    {
        var runbook = Runbook();
        runbook.IsEnabled = false;

        Assert.Equal(
            RunbookVerdict.Disabled,
            RunbookRateLimit.Evaluate(runbook, new RunbookOptions(), 0, isAutomatic: false).Verdict);
    }

    [Fact]
    public void WindowStart_IsTheRunbooksOwnWindowBeforeNow()
    {
        var now = new DateTimeOffset(2026, 8, 17, 12, 0, 0, TimeSpan.Zero);
        var runbook = Runbook();
        runbook.RateLimitWindowMinutes = 30;

        Assert.Equal(now.AddMinutes(-30), RunbookRateLimit.WindowStart(runbook, now));
    }

    private static Runbook Runbook(int allowance = 5) => new()
    {
        Id = Guid.CreateVersion7(),
        Key = RunbookCatalog.RestartService,
        Name = "Restart a service",
        TimeoutSeconds = 60,
        MaxExecutionsPerWindow = allowance,
        RateLimitWindowMinutes = 60,
        IsEnabled = true,
        CreatedBy = "test",
        UpdatedBy = "test",
    };
}
