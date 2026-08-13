using Modules.Assets.Features.Import;
using Modules.Assets.Features.Software;

namespace Infrastructure.Tests;

/// <summary>
/// Reading an inventory file into rows: which headers are recognised, and what a row is refused for.
/// No database — the planner does not know which CIs exist.
/// </summary>
public sealed class SoftwareImportPlannerTests
{
    [Theory]
    [InlineData("Asset Tag")]
    [InlineData("asset_tag")]
    [InlineData("ASSETTAG")]
    [InlineData("tag")]
    public void Plan_RecognisesTheMachineColumnHoweverTheExportSpellsIt(string header)
    {
        var plan = SoftwareImportPlanner.Plan(Table([header, "Software"], [["LT-0001", "Google Chrome"]]));

        Assert.True(plan.IsSuccess);
        Assert.Equal("LT-0001", Assert.Single(plan.Rows).AssetTag);
    }

    [Theory]
    [InlineData("Software")]
    [InlineData("Display Name")]
    [InlineData("product")]
    [InlineData("Application")]
    public void Plan_RecognisesTheSoftwareColumnHoweverTheExportSpellsIt(string header)
    {
        var plan = SoftwareImportPlanner.Plan(Table(["Serial number", header], [["5CG4101001", "Google Chrome"]]));

        Assert.True(plan.IsSuccess);
        var row = Assert.Single(plan.Rows);
        Assert.Equal("Google Chrome", row.SoftwareName);
        Assert.Equal("5CG4101001", row.SerialNumber);
    }

    [Fact]
    public void Plan_ReadsEveryOptionalColumnItSupports()
    {
        var plan = SoftwareImportPlanner.Plan(Table(
            ["hostname", "software", "publisher", "version", "installed on"],
            [["dc1-app-01", "Microsoft Office", "Microsoft Corporation", "16.0.14332", "2026-07-14"]]));

        var row = Assert.Single(plan.Rows);
        Assert.Equal("dc1-app-01", row.Hostname);
        Assert.Equal("Microsoft Corporation", row.Publisher);
        Assert.Equal("16.0.14332", row.Version);
        Assert.Equal(new DateOnly(2026, 7, 14), row.InstalledOn);
        Assert.Empty(row.Errors);
    }

    /// <summary>The failure path a file with the wrong shape takes: refused whole, naming what is read.</summary>
    [Fact]
    public void Plan_AFileWithNoMachineColumn_IsRefusedWithTheColumnsItReads()
    {
        var plan = SoftwareImportPlanner.Plan(Table(["software", "version"], [["Google Chrome", "121.0"]]));

        Assert.False(plan.IsSuccess);
        Assert.Contains("no column naming the machine", plan.Error);
        Assert.Contains("asset tag", plan.Error);
        Assert.Empty(plan.Rows);
    }

    [Fact]
    public void Plan_AFileWithNoSoftwareColumn_IsRefusedWholeRatherThanRowByRow()
    {
        var plan = SoftwareImportPlanner.Plan(Table(["asset tag", "version"], [["LT-0001", "121.0"]]));

        Assert.False(plan.IsSuccess);
        Assert.Contains("no column naming the software", plan.Error);
    }

    /// <summary>A bad row is that row's problem: the rest of the file still imports.</summary>
    [Fact]
    public void Plan_ABlankSoftwareNameOrMachine_FailsOnlyThatRow()
    {
        var plan = SoftwareImportPlanner.Plan(Table(
            ["asset tag", "software"],
            [["LT-0001", "Google Chrome"], ["LT-0002", ""], ["", "Mozilla Firefox"]]));

        Assert.True(plan.IsSuccess);
        Assert.Equal(3, plan.Rows.Count);
        Assert.Empty(plan.Rows[0].Errors);
        Assert.Contains("software name is blank", Assert.Single(plan.Rows[1].Errors));
        Assert.Contains("names no machine", Assert.Single(plan.Rows[2].Errors));
    }

    [Fact]
    public void Plan_ADateItCannotRead_IsThatRowsErrorRatherThanAGuess()
    {
        var plan = SoftwareImportPlanner.Plan(Table(
            ["asset tag", "software", "installed on"],
            [["LT-0001", "Google Chrome", "last Tuesday"]]));

        var row = Assert.Single(plan.Rows);
        Assert.Null(row.InstalledOn);
        Assert.Contains("not a date this import can read", Assert.Single(row.Errors));
    }

    /// <summary>The line number is the one the operator sees in their own file: the header is line 1.</summary>
    [Fact]
    public void Plan_KeepsTheLineNumbersTheFileReaderGaveIt()
    {
        var plan = SoftwareImportPlanner.Plan(Table(
            ["asset tag", "software"],
            [["LT-0001", "A"], ["LT-0002", "B"]]));

        Assert.Equal([2, 3], plan.Rows.Select(row => row.LineNumber));
    }

    [Fact]
    public void Plan_ReportsTheMachineByWhicheverColumnNamedIt()
    {
        var plan = SoftwareImportPlanner.Plan(Table(
            ["hostname", "software"],
            [["dc1-app-01", "Google Chrome"]]));

        Assert.Equal("dc1-app-01", Assert.Single(plan.Rows).Machine);
    }

    private static CiImportTable Table(string[] headers, string[][] rows) => new(
        headers,
        [.. rows.Select((cells, index) => new CiImportRow(index + 2, cells))]);
}
