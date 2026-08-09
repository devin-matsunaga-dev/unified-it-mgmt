using Modules.Assets.Data;
using Modules.Assets.Features.Import;

namespace Infrastructure.Tests;

public sealed class CiImportPlannerTests
{
    private static readonly CiCustomField RackUnit = new()
    {
        Id = Guid.CreateVersion7(),
        CiType = CiType.Server,
        Key = "rackUnit",
        Label = "Rack unit",
        Type = CiCustomFieldType.Text,
        IsRequired = false,
        Options = [],
    };

    [Fact]
    public void TargetsFor_ListsCoreColumnsTypeAttributesAndCustomFields()
    {
        var targets = CiImportPlanner.TargetsFor(CiType.Server, [RackUnit]);

        Assert.Contains(targets, target => target.Key == "name" && target.IsRequired);
        Assert.Contains(targets, target => target.Key == "attributes.cpuCores" && target.IsRequired);
        Assert.Contains(targets, target => target.Key == "customFields.rackUnit");
        // A field of another type must never appear as a column of this one.
        Assert.DoesNotContain(targets, target => target.Key == "attributes.vendor");
    }

    [Fact]
    public void TargetsFor_Mixed_OffersTheTypeColumnAndTheUnionOfEveryTypesColumns()
    {
        var targets = CiImportPlanner.TargetsFor(null, [RackUnit]);

        Assert.Contains(targets, target => target.Key == "type" && !target.IsRequired);
        Assert.Contains(targets, target => target.Key == "attributes.manufacturer");
        Assert.Contains(targets, target => target.Key == "attributes.portCount");
        Assert.Contains(targets, target => target.Key == "customFields.rackUnit");
    }

    [Fact]
    public void TargetsFor_Mixed_CarriesRequirednessPerTypeRatherThanOnTheColumn()
    {
        var targets = CiImportPlanner.TargetsFor(null, []);

        // Shared by Server and Virtual, required by both — but never by a Hardware row, so the column
        // itself must not claim to be required.
        var hostname = Assert.Single(targets, target => target.Key == "attributes.hostname");
        Assert.False(hostname.IsRequired);
        Assert.Equal(
            [CiType.Server, CiType.Virtual],
            hostname.Types!.Select(entry => entry.Type).Order());
        Assert.All(hostname.Types!, entry => Assert.True(entry.IsRequired));

        var serviceTier = Assert.Single(targets, target => target.Key == "attributes.serviceTier");
        Assert.False(Assert.Single(serviceTier.Types!).IsRequired);
    }

    [Fact]
    public void TargetsFor_SingleType_CarriesNoPerTypeRequirements()
    {
        var targets = CiImportPlanner.TargetsFor(CiType.Server, [RackUnit]);

        Assert.DoesNotContain(targets, target => target.Key == "type");
        Assert.All(targets, target => Assert.Null(target.Types));
    }

    [Fact]
    public void ValidateMapping_Mixed_RejectsATargetNoTypeDeclares()
    {
        var errors = CiImportPlanner.ValidateMapping(
            new CiImportMapping(
                null,
                new Dictionary<string, string>
                {
                    ["name"] = "Name",
                    ["assetTag"] = "Tag",
                    ["attributes.invented"] = "Made up",
                }),
            CiImportPlanner.TargetsFor(null, []),
            ["Name", "Tag", "Made up"]);

        Assert.Contains(
            "is not a column of any CI type",
            Assert.Single(errors["mapping.attributes.invented"]));
    }

    [Fact]
    public void Extract_Mixed_ReadsTheTypeCell()
    {
        var mapping = new CiImportMapping(null, new Dictionary<string, string>
        {
            ["name"] = "Name",
            ["assetTag"] = "Tag",
            ["type"] = "Kind",
        });

        var values = CiImportPlanner.Extract(
            mapping, ["Name", "Tag", "Kind"], new CiImportRow(4, ["switch-1", "AT-9", "NetworkDevice"]));

        Assert.Equal("NetworkDevice", values.TypeCell);
    }

    [Fact]
    public void Suggest_MatchesOnKeyLabelAndAlias()
    {
        var targets = CiImportPlanner.TargetsFor(CiType.Server, [RackUnit]);

        var suggestion = CiImportPlanner.Suggest(
            targets, ["Name", "Asset Tag", "Service tag", "operating system", "Rack unit", "Unmapped column"]);

        Assert.Equal("Name", suggestion["name"]);
        Assert.Equal("Asset Tag", suggestion["assetTag"]);
        Assert.Equal("Service tag", suggestion["serialNumber"]);
        Assert.Equal("operating system", suggestion["attributes.operatingSystem"]);
        Assert.Equal("Rack unit", suggestion["customFields.rackUnit"]);
        Assert.DoesNotContain("Unmapped column", suggestion.Values);
    }

    [Fact]
    public void Suggest_TwoHeadersMatchingOneTarget_KeepsTheFirst()
    {
        var targets = CiImportPlanner.TargetsFor(CiType.Hardware, []);

        var suggestion = CiImportPlanner.Suggest(targets, ["Serial number", "Serial"]);

        Assert.Equal("Serial number", suggestion["serialNumber"]);
    }

    [Fact]
    public void ValidateMapping_WithoutADedupeKey_IsRejected()
    {
        var errors = Validate(new Dictionary<string, string> { ["name"] = "Name" }, ["Name"]);

        Assert.Contains("matched to existing CIs", Assert.Single(errors["mapping.assetTag"]));
    }

    [Fact]
    public void ValidateMapping_WithoutName_IsRejected()
    {
        var errors = Validate(new Dictionary<string, string> { ["assetTag"] = "Tag" }, ["Tag"]);

        Assert.Contains("mapping.name", errors.Keys);
    }

    [Fact]
    public void ValidateMapping_UnknownTargetOrMissingHeader_IsRejected()
    {
        var errors = Validate(
            new Dictionary<string, string>
            {
                ["name"] = "Name",
                ["assetTag"] = "Tag",
                ["attributes.portCount"] = "Ports",
                ["description"] = "Column that is not in the file",
            },
            ["Name", "Tag", "Ports"]);

        Assert.Contains("is not a column of a Hardware CI", Assert.Single(errors["mapping.attributes.portCount"]));
        Assert.Contains("has no column named", Assert.Single(errors["mapping.description"]));
    }

    [Fact]
    public void ValidateMapping_OneColumnFeedingTwoFields_IsRejected()
    {
        var errors = Validate(
            new Dictionary<string, string> { ["name"] = "Thing", ["assetTag"] = "Thing" }, ["Thing"]);

        Assert.Contains(errors.Values.SelectMany(value => value), error => error.Contains("more than one field"));
    }

    [Fact]
    public void ValidateMapping_AUsableMapping_HasNoErrors()
    {
        var errors = Validate(
            new Dictionary<string, string>
            {
                ["name"] = "Name",
                ["serialNumber"] = "Serial",
                ["attributes.manufacturer"] = "Make",
                ["attributes.model"] = "Model",
            },
            ["Name", "Serial", "Make", "Model"]);

        Assert.Empty(errors);
    }

    [Fact]
    public void Extract_ReadsMappedColumnsAndDropsBlanks()
    {
        var mapping = new CiImportMapping(CiType.Hardware, new Dictionary<string, string>
        {
            ["name"] = "Name",
            ["assetTag"] = "Tag",
            ["description"] = "Notes",
            ["attributes.manufacturer"] = "Make",
        });

        var values = CiImportPlanner.Extract(
            mapping, ["Name", "Tag", "Notes", "Make"], new CiImportRow(7, ["laptop-1", "AT-1", "  ", "Dell"]));

        Assert.Equal(7, values.LineNumber);
        Assert.Equal("laptop-1", values.Name);
        Assert.Equal("AT-1", values.AssetTag);
        // A blank cell is "no statement", so it must not reach the CI as an empty description.
        Assert.Null(values.Description);
        Assert.Null(values.SerialNumber);
        Assert.Equal("Dell", values.Attributes["manufacturer"]);
    }

    private static IReadOnlyDictionary<string, string[]> Validate(
        Dictionary<string, string> columns,
        string[] headers) =>
        CiImportPlanner.ValidateMapping(
            new CiImportMapping(CiType.Hardware, columns),
            CiImportPlanner.TargetsFor(CiType.Hardware, []),
            headers);
}
