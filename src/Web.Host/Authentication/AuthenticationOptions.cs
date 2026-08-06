namespace Web.Host.Authentication;

public sealed class AuthenticationOptions
{
    public const string SectionName = "Authentication";

    public string Authority { get; init; } = string.Empty;

    public string Audience { get; init; } = string.Empty;

    public string ClientId { get; init; } = string.Empty;

    public string PostLogoutRedirectUri { get; init; } = string.Empty;

    public bool RequireHttpsMetadata { get; init; } = true;
}
