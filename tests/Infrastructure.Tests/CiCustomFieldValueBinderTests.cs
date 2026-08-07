using Modules.Assets.Data;
using Modules.Assets.Features.Cis;

namespace Infrastructure.Tests;

public sealed class CiCustomFieldValueBinderTests
{
    private static CiCustomField Field(
        string key = "rack_unit",
        string label = "Rack unit",
        CiCustomFieldType type = CiCustomFieldType.Text,
        bool isRequired = false,
        params string[] options) => new()
    {
        Id = Guid.CreateVersion7(), CiType = CiType.Server, Key = key, Label = label,
        Type = type, IsRequired = isRequired, Options = [.. options],
    };

    [Fact]
    public void Bind_RequiredFieldMissing_ReportsFieldError()
    {
        var result = CiCustomFieldValueBinder.Bind(
            [Field(isRequired: true)], new Dictionary<string, string?>());

        Assert.Empty(result.Values);
        Assert.Equal(["Rack unit is required."], result.Errors["customFields.rack_unit"]);
    }

    [Fact]
    public void Bind_UnknownKey_ReportsFieldError()
    {
        var result = CiCustomFieldValueBinder.Bind(
            [Field()], new Dictionary<string, string?> { ["not_a_field"] = "x" });

        Assert.Equal(
            ["'not_a_field' is not a field of the selected CI type."],
            result.Errors["customFields.not_a_field"]);
    }

    [Fact]
    public void Bind_NumberField_CanonicalisesToInvariantCulture()
    {
        var field = Field(type: CiCustomFieldType.Number);

        var result = CiCustomFieldValueBinder.Bind(
            [field], new Dictionary<string, string?> { ["rack_unit"] = "42.50" });

        Assert.Equal("42.50", result.Values[field.Id]);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Bind_DateFieldWrongFormat_ReportsFieldError()
    {
        var field = Field(type: CiCustomFieldType.Date);

        var result = CiCustomFieldValueBinder.Bind(
            [field], new Dictionary<string, string?> { ["rack_unit"] = "07/08/2026" });

        Assert.Equal(["Rack unit must be a date in yyyy-MM-dd format."], result.Errors["customFields.rack_unit"]);
    }

    [Fact]
    public void Bind_SelectFieldUnlistedOption_ReportsFieldError()
    {
        var field = Field(type: CiCustomFieldType.Select, options: ["Primary", "Secondary"]);

        var result = CiCustomFieldValueBinder.Bind(
            [field], new Dictionary<string, string?> { ["rack_unit"] = "Tertiary" });

        Assert.Equal(
            ["Rack unit must be one of: Primary, Secondary."],
            result.Errors["customFields.rack_unit"]);
    }

    [Fact]
    public void Bind_SelectFieldDifferentCasing_CanonicalisesToDeclaredOption()
    {
        var field = Field(type: CiCustomFieldType.Select, options: ["Primary", "Secondary"]);

        var result = CiCustomFieldValueBinder.Bind(
            [field], new Dictionary<string, string?> { ["rack_unit"] = "pRIMARY" });

        Assert.Equal("Primary", result.Values[field.Id]);
    }
}
