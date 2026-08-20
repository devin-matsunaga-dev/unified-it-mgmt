using System.Text.Json;

using Modules.Assets.Features.DeviceIdentification;
using Modules.Assets.Features.DeviceIdentification.Cisco;

namespace Infrastructure.Tests;

/// <summary>
/// The Cisco mapper, against the response structure Cisco publishes on DevNet. Written from
/// documentation rather than from a captured sample — which was possible here and was not for Dell,
/// whose field names live in an SDK issued only after approval.
/// </summary>
public sealed class CiscoCoverageMapperTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    private const string Covered = """
    {
      "serial_numbers": [{
        "sr_no": "FDO12345678",
        "is_covered": "YES",
        "orderable_pid_list": [
          { "orderable_pid": "WS-C2960X-24TS-L", "item_description": "Catalyst 2960-X 24 GigE" }
        ],
        "warranty_end_date": "2027-03-12",
        "coverage_end_date": "2028-03-12"
      }]
    }
    """;

    [Fact]
    public void Map_ADocumentedResponse_ReadsTheProductAndItsDescription()
    {
        var result = new CiscoCoverageMapper().Map(Parse(Covered), "FDO12345678");

        Assert.NotNull(result);
        Assert.Equal("Cisco", result.Manufacturer);
        // The description is what a person recognises; the PID is the orderable code.
        Assert.Equal("Catalyst 2960-X 24 GigE", result.Model);
        Assert.Equal("WS-C2960X-24TS-L", result.ProductNumber);
        Assert.Equal("FDO12345678", result.SerialNumber);
        Assert.Equal(IdentificationConfidence.High, result.Confidence);
    }

    /// <summary>
    /// The API takes several serials at once. Matching positionally would attach one device's model
    /// to another's if the order ever differed from the request.
    /// </summary>
    [Fact]
    public void Map_AResponseCarryingSeveralDevices_TakesTheOneAskedAbout()
    {
        const string json = """
        {
          "serial_numbers": [
            { "sr_no": "OTHER0001", "orderable_pid_list": [{ "orderable_pid": "WRONG-PID" }] },
            { "sr_no": "FDO12345678", "orderable_pid_list": [{ "orderable_pid": "RIGHT-PID" }] }
          ]
        }
        """;

        var result = new CiscoCoverageMapper().Map(Parse(json), "FDO12345678");

        Assert.Equal("RIGHT-PID", result?.ProductNumber);
    }

    /// <summary>A PID with no description still names the product, so the model falls back to it.</summary>
    [Fact]
    public void Map_APidWithNoDescription_FallsBackToThePid()
    {
        const string json = """
        {"serial_numbers":[{"sr_no":"FDO1","orderable_pid_list":[{"orderable_pid":"AIR-AP1815I-E-K9"}]}]}
        """;

        Assert.Equal("AIR-AP1815I-E-K9", new CiscoCoverageMapper().Map(Parse(json), "FDO1")?.Model);
    }

    /// <summary>A device Cisco does not know comes back as an entry with no PID list, not an error.</summary>
    [Fact]
    public void Map_ADeviceWithNoProductIdentifier_IdentifiesNothing()
    {
        const string json = """
        {"serial_numbers":[{"sr_no":"FDO1","is_covered":"NO","orderable_pid_list":[]}]}
        """;

        Assert.Null(new CiscoCoverageMapper().Map(Parse(json), "FDO1"));
    }

    [Fact]
    public void Map_AResponseForADifferentSerial_IdentifiesNothing()
    {
        var result = new CiscoCoverageMapper().Map(Parse(Covered), "SOMETHINGELSE");

        Assert.Null(result);
    }

    /// <summary>
    /// A shape that is nothing like the documented one must produce an unidentified device rather
    /// than an exception — a technician has to be able to register the switch either way.
    /// </summary>
    [Theory]
    [InlineData("""{"unexpected":true}""")]
    [InlineData("""{"serial_numbers":"not-an-array"}""")]
    [InlineData("""{"serial_numbers":[{"sr_no":"FDO1"}]}""")]
    [InlineData("""[]""")]
    [InlineData("""null""")]
    public void Map_AnUnexpectedShape_IdentifiesNothingRatherThanThrowing(string json)
    {
        Assert.Null(new CiscoCoverageMapper().Map(Parse(json), "FDO1"));
    }
}
