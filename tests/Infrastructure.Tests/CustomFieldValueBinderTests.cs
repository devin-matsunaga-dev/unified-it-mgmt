using Modules.Helpdesk.Data;
using Modules.Helpdesk.Features.Categories;

namespace Infrastructure.Tests;

public sealed class CustomFieldValueBinderTests
{
    private static TicketCustomField Field(
        string key = "asset_tag",
        string label = "Asset tag",
        CustomFieldType type = CustomFieldType.Text,
        bool isRequired = false,
        params string[] options) => new()
    {
        Id = Guid.CreateVersion7(), CategoryId = Guid.CreateVersion7(), Key = key, Label = label,
        Type = type, IsRequired = isRequired, Options = [.. options],
    };

    [Fact]
    public void Bind_RequiredFieldMissing_ReportsFieldError()
    {
        var field = Field(isRequired: true);

        var result = CustomFieldValueBinder.Bind([field], new Dictionary<string, string?>());

        Assert.Empty(result.Values);
        Assert.Equal(["Asset tag is required."], result.Errors["customFields.asset_tag"]);
    }

    [Fact]
    public void Bind_RequiredFieldWhitespaceOnly_ReportsFieldError()
    {
        var field = Field(isRequired: true);

        var result = CustomFieldValueBinder.Bind(
            [field], new Dictionary<string, string?> { ["asset_tag"] = "   " });

        Assert.Equal(["Asset tag is required."], result.Errors["customFields.asset_tag"]);
    }

    [Fact]
    public void Bind_OptionalFieldOmitted_ProducesNoValueAndNoError()
    {
        var result = CustomFieldValueBinder.Bind([Field()], new Dictionary<string, string?>());

        Assert.Empty(result.Values);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Bind_UnknownKey_ReportsFieldError()
    {
        var result = CustomFieldValueBinder.Bind(
            [Field()], new Dictionary<string, string?> { ["not_a_field"] = "value" });

        Assert.Equal(
            ["'not_a_field' is not a field of the selected category."],
            result.Errors["customFields.not_a_field"]);
    }

    [Theory]
    [InlineData(CustomFieldType.Number, "twelve", "Value must be a number.")]
    [InlineData(CustomFieldType.Date, "07/08/2026", "Value must be a date in yyyy-MM-dd format.")]
    public void Bind_MalformedValue_ReportsTypeError(CustomFieldType type, string value, string expected)
    {
        var field = Field(key: "value", label: "Value", type: type);

        var result = CustomFieldValueBinder.Bind(
            [field], new Dictionary<string, string?> { ["value"] = value });

        Assert.Empty(result.Values);
        Assert.Equal([expected], result.Errors["customFields.value"]);
    }

    [Fact]
    public void Bind_SelectValueOutsideOptions_ReportsAllowedOptions()
    {
        var field = Field(key: "floor", label: "Floor", type: CustomFieldType.Select, options: ["First", "Second"]);

        var result = CustomFieldValueBinder.Bind(
            [field], new Dictionary<string, string?> { ["floor"] = "Third" });

        Assert.Equal(["Floor must be one of: First, Second."], result.Errors["customFields.floor"]);
    }

    [Fact]
    public void Bind_ValidValues_CanonicalisesEveryFieldType()
    {
        var text = Field();
        var number = Field(key: "count", label: "Count", type: CustomFieldType.Number);
        var date = Field(key: "seen_on", label: "Seen on", type: CustomFieldType.Date);
        var select = Field(key: "floor", label: "Floor", type: CustomFieldType.Select, options: ["First"]);

        var result = CustomFieldValueBinder.Bind(
            [text, number, date, select],
            new Dictionary<string, string?>
            {
                ["asset_tag"] = "  LT-4417 ", ["count"] = "12.50", ["seen_on"] = "2026-08-07", ["floor"] = "first",
            });

        Assert.Empty(result.Errors);
        Assert.Equal("LT-4417", result.Values[text.Id]);
        Assert.Equal("12.50", result.Values[number.Id]);
        Assert.Equal("2026-08-07", result.Values[date.Id]);
        Assert.Equal("First", result.Values[select.Id]);
    }

    [Fact]
    public void Bind_TextLongerThanLimit_ReportsLengthError()
    {
        var result = CustomFieldValueBinder.Bind(
            [Field()], new Dictionary<string, string?> { ["asset_tag"] = new string('a', 1_001) });

        Assert.Equal(["Asset tag must be 1000 characters or fewer."], result.Errors["customFields.asset_tag"]);
    }
}
