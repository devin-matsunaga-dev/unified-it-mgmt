using Modules.Assets.Data;
using Modules.Assets.Features.Cis;

namespace Infrastructure.Tests;

public sealed class CiTypeSchemaTests
{
    private static Dictionary<string, string?> ServerAttributes() => new()
    {
        ["hostname"] = "app-01",
        ["operatingSystem"] = "Ubuntu 24.04",
        ["cpuCores"] = "8",
        ["ramGb"] = "32",
    };

    [Theory]
    [InlineData(CiType.Hardware)]
    [InlineData(CiType.Server)]
    [InlineData(CiType.NetworkDevice)]
    [InlineData(CiType.Software)]
    [InlineData(CiType.Virtual)]
    [InlineData(CiType.Logical)]
    public void For_EveryCiType_DeclaresAtLeastOneRequiredAttribute(CiType type)
    {
        var definitions = CiTypeSchema.For(type);

        Assert.NotEmpty(definitions);
        Assert.Contains(definitions, definition => definition.IsRequired);
    }

    [Fact]
    public void Bind_ServerWithCompleteAttributes_CanonicalisesValues()
    {
        var result = CiTypeSchema.Bind(CiType.Server, ServerAttributes());

        Assert.Empty(result.Errors);
        Assert.Equal("app-01", result.Values["hostname"]);
        Assert.Equal("8", result.Values["cpuCores"]);
        Assert.Equal("32", result.Values["ramGb"]);
    }

    [Fact]
    public void Bind_ServerMissingRequiredAttribute_ReportsAttributeError()
    {
        var attributes = ServerAttributes();
        attributes.Remove("cpuCores");

        var result = CiTypeSchema.Bind(CiType.Server, attributes);

        Assert.Equal(["CPU cores is required for a Server CI."], result.Errors["attributes.cpuCores"]);
    }

    [Fact]
    public void Bind_RequiredAttributeWhitespaceOnly_ReportsAttributeError()
    {
        var attributes = ServerAttributes();
        attributes["hostname"] = "   ";

        var result = CiTypeSchema.Bind(CiType.Server, attributes);

        Assert.Equal(["Hostname is required for a Server CI."], result.Errors["attributes.hostname"]);
    }

    [Fact]
    public void Bind_AttributeBelongingToAnotherType_IsRejectedRatherThanIgnored()
    {
        var attributes = ServerAttributes();
        attributes["portCount"] = "48";

        var result = CiTypeSchema.Bind(CiType.Server, attributes);

        Assert.Equal(["'portCount' is not an attribute of a Server CI."], result.Errors["attributes.portCount"]);
    }

    [Theory]
    [InlineData("not-a-number")]
    [InlineData("-1")]
    [InlineData("8.5")]
    public void Bind_NonIntegerAttributeValue_ReportsAttributeError(string value)
    {
        var attributes = ServerAttributes();
        attributes["cpuCores"] = value;

        var result = CiTypeSchema.Bind(CiType.Server, attributes);

        Assert.Equal(["CPU cores must be a whole number of zero or more."], result.Errors["attributes.cpuCores"]);
    }

    [Fact]
    public void Bind_MalformedManagementIp_ReportsAttributeError()
    {
        var result = CiTypeSchema.Bind(CiType.NetworkDevice, new Dictionary<string, string?>
        {
            ["managementIp"] = "10.0.0.999",
            ["vendor"] = "Cisco",
            ["portCount"] = "48",
        });

        Assert.Equal(
            ["Management IP must be a valid IPv4 or IPv6 address."],
            result.Errors["attributes.managementIp"]);
    }

    [Fact]
    public void Bind_IpAddressAttribute_IsCanonicalised()
    {
        var result = CiTypeSchema.Bind(CiType.NetworkDevice, new Dictionary<string, string?>
        {
            ["managementIp"] = "2001:0db8:0000:0000:0000:0000:0000:0001",
            ["vendor"] = "Juniper",
            ["portCount"] = "24",
        });

        Assert.Empty(result.Errors);
        Assert.Equal("2001:db8::1", result.Values["managementIp"]);
    }

    [Fact]
    public void Bind_OptionalAttributeOmitted_ProducesNoValueAndNoError()
    {
        var result = CiTypeSchema.Bind(CiType.Logical, new Dictionary<string, string?>
        {
            ["purpose"] = "Payroll processing",
        });

        Assert.Empty(result.Errors);
        Assert.False(result.Values.ContainsKey("serviceTier"));
    }
}
