using Modules.Assets.Data;
using Modules.Assets.Features.Software;

namespace Infrastructure.Tests;

/// <summary>
/// The normalisation catalogue's whole decision procedure, without a database: what a raw name is
/// compared as, which rule wins when several match, and what makes two installs the same install.
/// </summary>
public sealed class SoftwareNormaliserTests
{
    private static readonly Guid Office = Guid.Parse("00000000-0000-0000-0000-0000000000f1");
    private static readonly Guid Office2021 = Guid.Parse("00000000-0000-0000-0000-0000000000f2");
    private static readonly Guid Anything = Guid.Parse("00000000-0000-0000-0000-0000000000f3");

    [Theory]
    [InlineData("  Microsoft   Office  ", "microsoft office")]
    [InlineData("MICROSOFT OFFICE", "microsoft office")]
    [InlineData("Microsoft\tOffice\n", "microsoft office")]
    [InlineData("   ", "")]
    [InlineData(null, "")]
    public void Canonicalise_TrimsCollapsesAndLowers(string? raw, string expected) =>
        Assert.Equal(expected, SoftwareNormaliser.Canonicalise(raw));

    [Fact]
    public void Match_AnExactRule_BeatsAPrefixAndAContains()
    {
        SoftwareRule[] rules =
        [
            new(Anything, SoftwareMatchKind.Contains, "office", 0),
            new(Office, SoftwareMatchKind.Prefix, "microsoft office", 0),
            new(Office2021, SoftwareMatchKind.Exact, "microsoft office professional plus 2021", 0),
        ];

        Assert.Equal(Office2021, SoftwareNormaliser.Match("Microsoft Office Professional Plus 2021", rules));
    }

    [Fact]
    public void Match_APrefixRule_BeatsAContainsEvenWhenTheContainsIsLonger()
    {
        SoftwareRule[] rules =
        [
            new(Anything, SoftwareMatchKind.Contains, "office professional plus", 0),
            new(Office, SoftwareMatchKind.Prefix, "microsoft office", 0),
        ];

        Assert.Equal(Office, SoftwareNormaliser.Match("Microsoft Office Professional Plus 2021 - en-us", rules));
    }

    [Fact]
    public void Match_WithinOneKind_TakesTheOperatorsPriorityThenTheLongerPattern()
    {
        SoftwareRule[] rules =
        [
            new(Anything, SoftwareMatchKind.Prefix, "microsoft", 5),
            new(Office, SoftwareMatchKind.Prefix, "microsoft office", 1),
        ];

        Assert.Equal(Office, SoftwareNormaliser.Match("Microsoft Office Professional Plus", rules));

        // Same priority: the longer pattern is the more specific statement about the name.
        SoftwareRule[] samePriority =
        [
            new(Anything, SoftwareMatchKind.Prefix, "microsoft", 0),
            new(Office, SoftwareMatchKind.Prefix, "microsoft office", 0),
        ];

        Assert.Equal(Office, SoftwareNormaliser.Match("Microsoft Office Professional Plus", samePriority));
    }

    [Fact]
    public void Match_TheOrderOfTheRulesGiven_DoesNotChangeTheAnswer()
    {
        SoftwareRule[] rules =
        [
            new(Anything, SoftwareMatchKind.Contains, "office", 0),
            new(Office, SoftwareMatchKind.Prefix, "microsoft office", 0),
        ];

        var forwards = SoftwareNormaliser.Match("Microsoft Office", rules);
        var backwards = SoftwareNormaliser.Match("Microsoft Office", rules.Reverse().ToArray());

        Assert.Equal(forwards, backwards);
    }

    /// <summary>The failure path the unrecognised list is built on: no rule claims it, and nothing is guessed.</summary>
    [Fact]
    public void Match_ANameNoRuleClaims_IsNull()
    {
        SoftwareRule[] rules = [new(Office, SoftwareMatchKind.Prefix, "microsoft office", 0)];

        Assert.Null(SoftwareNormaliser.Match("Contoso VPN Client 4.2.7", rules));
        Assert.Null(SoftwareNormaliser.Match("   ", rules));
        Assert.Null(SoftwareNormaliser.Match("Microsoft Office", []));
    }

    [Fact]
    public void Match_APrefixRule_DoesNotMatchTheNameInTheMiddle()
    {
        SoftwareRule[] rules = [new(Office, SoftwareMatchKind.Prefix, "office", 0)];

        Assert.Null(SoftwareNormaliser.Match("Microsoft Office", rules));
        Assert.Equal(Office, SoftwareNormaliser.Match("Office 365", rules));
    }

    [Fact]
    public void IdentityKey_SeparatesTwoVersionsOfOneProductAndJoinsTwoSpellingsOfOne()
    {
        Assert.NotEqual(
            SoftwareNormaliser.IdentityKeyFor("Google Chrome", "121.0"),
            SoftwareNormaliser.IdentityKeyFor("Google Chrome", "122.0"));
        Assert.Equal(
            SoftwareNormaliser.IdentityKeyFor("Google  Chrome ", "121.0"),
            SoftwareNormaliser.IdentityKeyFor("google chrome", "121.0"));

        // A missing version is a value like any other here, which is the point: two nullable columns
        // could not carry this in a unique index because Postgres treats two nulls as distinct.
        Assert.Equal(
            SoftwareNormaliser.IdentityKeyFor("Google Chrome", null),
            SoftwareNormaliser.IdentityKeyFor("Google Chrome", "  "));
        Assert.NotEqual(
            SoftwareNormaliser.IdentityKeyFor("Google Chrome", null),
            SoftwareNormaliser.IdentityKeyFor("Google Chrome", "121.0"));
    }
}
