using Modules.Assets.Data;
using Modules.Assets.Features.Import;

namespace Infrastructure.Tests;

public sealed class CiImportTypeResolverTests
{
    [Fact]
    public void Resolve_SingleTypeImport_UsesTheChosenTypeForEveryRow()
    {
        var resolution = CiImportTypeResolver.Resolve(
            Mapping(CiType.Hardware, ["name", "assetTag"]),
            Row(attributes: new Dictionary<string, string?> { ["hypervisor"] = "VMware ESXi" }));

        // Even a row full of another type's columns: the operator declared the whole file.
        Assert.Equal(CiType.Hardware, resolution.Type);
        Assert.Equal(CiImportTypeSource.Fixed, resolution.Source);
        Assert.Null(resolution.Error);
    }

    [Theory]
    [InlineData("Server", CiType.Server)]
    [InlineData("NetworkDevice", CiType.NetworkDevice)]
    [InlineData("network device", CiType.NetworkDevice)]
    [InlineData(" virtual ", CiType.Virtual)]
    public void Resolve_MappedTypeColumn_ReadsTheCellAsAnOperatorWouldWriteIt(string cell, CiType expected)
    {
        var resolution = CiImportTypeResolver.Resolve(Mapping(null, ["name", "assetTag", "type"]), Row(typeCell: cell));

        Assert.Equal(expected, resolution.Type);
        Assert.Equal(CiImportTypeSource.Column, resolution.Source);
    }

    [Fact]
    public void Resolve_MappedTypeColumnStatingSomethingElse_IsRefused()
    {
        var resolution = CiImportTypeResolver.Resolve(
            Mapping(null, ["name", "assetTag", "type"]), Row(typeCell: "Photocopier"));

        Assert.Null(resolution.Type);
        Assert.Contains("'Photocopier' is not a CI type", resolution.Error);
    }

    [Fact]
    public void Resolve_MappedTypeColumnLeftBlank_IsRefusedRatherThanGuessed()
    {
        // The row still carries a Hardware discriminator, but a mapped type column is the operator's
        // statement of where the type comes from, so a blank cell is a missing answer, not an invitation.
        var resolution = CiImportTypeResolver.Resolve(
            Mapping(null, ["name", "assetTag", "type"]),
            Row(attributes: new Dictionary<string, string?> { ["manufacturer"] = "Dell" }));

        Assert.Null(resolution.Type);
        Assert.Contains("blank", resolution.Error);
    }

    [Theory]
    [InlineData("manufacturer", CiType.Hardware)]
    [InlineData("operatingSystem", CiType.Server)]
    [InlineData("managementIp", CiType.NetworkDevice)]
    [InlineData("version", CiType.Software)]
    [InlineData("hypervisor", CiType.Virtual)]
    [InlineData("purpose", CiType.Logical)]
    public void Resolve_NoTypeColumn_InfersFromAColumnOnlyOneTypeDeclares(string key, CiType expected)
    {
        var resolution = CiImportTypeResolver.Resolve(
            Mapping(null, ["name", "assetTag"]),
            Row(attributes: new Dictionary<string, string?> { [key] = "something" }));

        Assert.Equal(expected, resolution.Type);
        Assert.Equal(CiImportTypeSource.Inferred, resolution.Source);
        Assert.Null(resolution.Error);
    }

    [Fact]
    public void Resolve_RowFillingOnlySharedColumns_IsRefused()
    {
        // hostname and ramGb belong to both Server and Virtual, so neither says which this row is.
        var resolution = CiImportTypeResolver.Resolve(
            Mapping(null, ["name", "assetTag"]),
            Row(attributes: new Dictionary<string, string?> { ["hostname"] = "app-01", ["ramGb"] = "32" }));

        Assert.Null(resolution.Type);
        Assert.Contains("could not be guessed", resolution.Error);
    }

    [Fact]
    public void Resolve_RowFillingTwoTypesColumns_IsRefusedAsAmbiguous()
    {
        var resolution = CiImportTypeResolver.Resolve(
            Mapping(null, ["name", "assetTag"]),
            Row(attributes: new Dictionary<string, string?>
            {
                ["cpuCores"] = "8",
                ["hypervisor"] = "VMware ESXi",
            }));

        Assert.Null(resolution.Type);
        Assert.Contains("ambiguous", resolution.Error);
        Assert.Contains("Server and Virtual", resolution.Error);
    }

    [Fact]
    public void Resolve_BlankCellsNeverReachHere_SoAnEmptyRowIsRefused()
    {
        var resolution = CiImportTypeResolver.Resolve(Mapping(null, ["name", "assetTag"]), Row());

        Assert.Null(resolution.Type);
        Assert.Contains("could not be guessed", resolution.Error);
    }

    private static CiImportMapping Mapping(CiType? type, string[] targetKeys) =>
        new(type, targetKeys.ToDictionary(key => key, key => $"{key} column", StringComparer.Ordinal));

    private static CiImportRowValues Row(
        IReadOnlyDictionary<string, string?>? attributes = null,
        string? typeCell = null) =>
        new(
            2,
            "thing-1",
            "AT-1",
            null,
            null,
            attributes ?? new Dictionary<string, string?>(StringComparer.Ordinal),
            new Dictionary<string, string?>(StringComparer.Ordinal),
            typeCell);
}
