namespace Platform.Vault;

public sealed class VaultOptions
{
    public const string SectionName = "Platform:Vault";

    /// <summary>
    /// How long a credential grant is redeemable for. Short on purpose: the poller asks for one and
    /// spends it in the same cycle, so the window only has to cover a request round trip and a clock
    /// skew. Two minutes rather than seconds because a poller and the API can disagree about the time
    /// by more than a request takes, and an expired-on-arrival grant is an outage nobody can debug.
    /// </summary>
    public int GrantLifetimeSeconds { get; set; } = 120;
}
