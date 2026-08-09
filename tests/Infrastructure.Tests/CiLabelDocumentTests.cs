using System.Text;

using Modules.Assets.Data;
using Modules.Assets.Features.Labels;

namespace Infrastructure.Tests;

/// <summary>
/// WP-2.7: the renderer itself. The assertions are deliberately structural — that a real PDF comes
/// out, for every size, for a single label and for a sheet long enough to need a second page — because
/// what the labels look like is a thing to hold up to a printer, not to assert on in a test.
/// </summary>
public sealed class CiLabelDocumentTests
{
    [Theory]
    [InlineData(CiLabelSize.Standard)]
    [InlineData(CiLabelSize.Small)]
    public void Render_ForOneCi_ProducesAPdf(CiLabelSize size)
    {
        var pdf = CiLabelDocument.Render([Label("Reception laptop", "LT-00421", "5CD1234ABC")], size);

        AssertIsPdf(pdf);
    }

    [Theory]
    [InlineData(CiLabelSize.Standard)]
    [InlineData(CiLabelSize.Small)]
    public void Render_ForMoreLabelsThanFitOnAPage_ProducesAMultiPageSheet(CiLabelSize size)
    {
        var labels = Enumerable.Range(1, 60)
            .Select(index => Label($"Warehouse laptop {index}", $"LT-{index:0000}", $"SN{index:0000}"))
            .ToList();

        var pdf = CiLabelDocument.Render(labels, size);

        AssertIsPdf(pdf);
        Assert.True(PageCount(pdf) > 1, "A sheet of 60 labels should run onto a second page.");
    }

    /// <summary>A CI need not carry either identifier, and the label still has to render.</summary>
    [Fact]
    public void Render_ForACiWithNoAssetTagOrSerial_StillProducesAPdf()
    {
        AssertIsPdf(CiLabelDocument.Render([Label("Unlabelled switch", null, null)], CiLabelSize.Standard));
    }

    [Fact]
    public void Render_ForALabelWithNoIdentifiersAndAVeryLongName_StillProducesAPdf()
    {
        var pdf = CiLabelDocument.Render(
            [Label(new string('x', 400), null, null)], CiLabelSize.Small);

        AssertIsPdf(pdf);
    }

    [Fact]
    public void Fit_LeavesAValueThatAlreadyFitsAlone()
    {
        Assert.Equal("Reception laptop", CiLabelDocument.Fit("Reception laptop", 34));
    }

    [Fact]
    public void Fit_TrimsALongValueToTheLimitWithAnEllipsis()
    {
        var fitted = CiLabelDocument.Fit("Second floor east wing reception desk laptop", 20);

        Assert.Equal(20, fitted.Length);
        Assert.EndsWith("…", fitted, StringComparison.Ordinal);
    }

    private static CiLabel Label(string name, string? assetTag, string? serial) => new(
        Guid.CreateVersion7(),
        name,
        assetTag,
        serial,
        CiType.Hardware,
        CiLabelCodes.PayloadFor("http://192.168.1.20:5173", Guid.CreateVersion7()));

    private static void AssertIsPdf(byte[] content)
    {
        Assert.NotEmpty(content);
        Assert.Equal("%PDF", Encoding.ASCII.GetString(content, 0, 4));
    }

    /// <summary>Counts page objects in the file rather than parsing it — enough to prove pagination ran.</summary>
    private static int PageCount(byte[] pdf)
    {
        var text = Encoding.Latin1.GetString(pdf);
        var count = 0;
        for (var index = text.IndexOf("/Type /Page", StringComparison.Ordinal); index >= 0;
             index = text.IndexOf("/Type /Page", index + 1, StringComparison.Ordinal))
        {
            if (!text.AsSpan(index).StartsWith("/Type /Pages", StringComparison.Ordinal))
            {
                count++;
            }
        }

        return count;
    }
}
