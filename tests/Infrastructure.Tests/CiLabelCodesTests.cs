using Modules.Assets.Features.Labels;

namespace Infrastructure.Tests;

/// <summary>
/// WP-2.7: what a printed QR carries, and what the scan page can read back out of it. The round trip
/// is the property that matters — a label nobody can resolve is a sticker.
/// </summary>
public sealed class CiLabelCodesTests
{
    private static readonly Guid CiId = Guid.Parse("0198f2c4-1e4a-7c2d-9a3b-4c5d6e7f8a9b");

    [Fact]
    public void PayloadFor_IsTheAbsoluteAssetUrl()
    {
        Assert.Equal(
            $"https://it.example.test/assets/{CiId}",
            CiLabelCodes.PayloadFor("https://it.example.test", CiId));
    }

    [Fact]
    public void PayloadFor_WithATrailingSlash_DoesNotDoubleIt()
    {
        Assert.Equal(
            $"https://it.example.test/assets/{CiId}",
            CiLabelCodes.PayloadFor("https://it.example.test/", CiId));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void PayloadFor_WithNothingConfigured_FallsBackToTheDevelopmentOrigin(string? baseUrl)
    {
        Assert.Equal($"{CiLabelCodes.DefaultBaseUrl}/assets/{CiId}", CiLabelCodes.PayloadFor(baseUrl, CiId));
    }

    [Fact]
    public void TryReadCiId_ForAPrintedLabel_ReadsBackTheCiItWasPrintedFor()
    {
        var payload = CiLabelCodes.PayloadFor("https://it.example.test", CiId);

        Assert.True(CiLabelCodes.TryReadCiId(payload, out var read));
        Assert.Equal(CiId, read);
    }

    [Theory]
    [InlineData("0198f2c4-1e4a-7c2d-9a3b-4c5d6e7f8a9b")]
    [InlineData("  0198f2c4-1e4a-7c2d-9a3b-4c5d6e7f8a9b  ")]
    [InlineData("http://192.168.1.20:5173/assets/0198f2c4-1e4a-7c2d-9a3b-4c5d6e7f8a9b")]
    [InlineData("http://192.168.1.20:5173/assets/0198f2c4-1e4a-7c2d-9a3b-4c5d6e7f8a9b/")]
    public void TryReadCiId_ReadsEveryFormAScannerProduces(string code)
    {
        Assert.True(CiLabelCodes.TryReadCiId(code, out var read));
        Assert.Equal(CiId, read);
    }

    /// <summary>
    /// An asset tag is not an id, and saying so is what sends the lookup on to match a serial number
    /// or a tag in the database instead of answering "not found".
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("LT-00421")]
    [InlineData("https://it.example.test/assets")]
    [InlineData("https://it.example.test/assets/not-a-guid")]
    [InlineData("mailto:someone@example.test")]
    public void TryReadCiId_ForAnythingThatIsNotAnId_ReturnsFalse(string? code)
    {
        Assert.False(CiLabelCodes.TryReadCiId(code, out var read));
        Assert.Equal(Guid.Empty, read);
    }
}
