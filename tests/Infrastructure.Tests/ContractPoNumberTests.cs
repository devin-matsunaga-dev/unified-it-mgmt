using Modules.Assets.Features.Contracts;

namespace Infrastructure.Tests;

/// <summary>
/// The PO prefix is applied on the server, not in the browser, so every caller lands on the same
/// value — and the uniqueness check compares like with like. These are the shapes a person types.
/// </summary>
public sealed class ContractPoNumberTests
{
    [Theory]
    [InlineData("22-0419", "PO - 22-0419")]
    [InlineData("  22-0419  ", "PO - 22-0419")]
    public void Normalise_ABareNumber_GainsThePrefix(string typed, string expected)
    {
        Assert.Equal(expected, ContractService.NormalisePoNumber(typed));
    }

    /// <summary>
    /// The regression the prefix invites: somebody typing it out of habit. Without stripping first
    /// they would get "PO - PO - 22-0419", which would not collide with the same purchase order
    /// entered by somebody who left the prefix off — so a duplicate would slip through the unique
    /// index precisely because the two were typed differently.
    /// </summary>
    [Theory]
    [InlineData("PO - 22-0419")]
    [InlineData("PO-22-0419")]
    [InlineData("PO 22-0419")]
    [InlineData("po - 22-0419")]
    [InlineData("PO: 22-0419")]
    [InlineData("P O - 22-0419")]
    public void Normalise_APrefixAlreadyTyped_IsNotDoubled(string typed)
    {
        Assert.Equal("PO - 22-0419", ContractService.NormalisePoNumber(typed));
    }

    /// <summary>
    /// Documented rather than claimed correct: the separator is what distinguishes a prefix from a
    /// word, and "POLARIS" has none. Recorded so the behaviour is a decision somebody made rather
    /// than a surprise, if a purchase order ever legitimately begins with those letters.
    /// </summary>
    [Fact]
    public void Normalise_AWordBeginningPo_IsSplitAndThisIsAKnownLimit()
    {
        Assert.Equal("PO - LARIS-7", ContractService.NormalisePoNumber("POLARIS-7"));
    }

    [Fact]
    public void Normalise_NothingButAPrefix_IsLeftForTheValidatorToRefuse()
    {
        Assert.Equal("PO", ContractService.NormalisePoNumber("PO"));
    }
}
