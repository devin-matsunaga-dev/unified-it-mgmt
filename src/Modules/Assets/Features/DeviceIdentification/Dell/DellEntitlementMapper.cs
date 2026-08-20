using System.Text.Json;

namespace Modules.Assets.Features.DeviceIdentification.Dell;

/// <summary>
/// Turns Dell's asset-entitlements response into the shape this module understands.
/// <para>
/// Behind an interface so the transport around it — token acquisition, timeouts, rate limiting,
/// catalogue write-through — is testable without a real response to hand, and so that the mapping
/// itself is one class to replace rather than a change threaded through a provider.
/// </para>
/// </summary>
public interface IDellEntitlementMapper
{
    /// <summary>
    /// Reads one device out of a response body, or null when the body does not describe one. Never
    /// throws on an unexpected shape: an answer that cannot be read is an unidentified device, and a
    /// technician must still be able to register it by hand.
    /// </summary>
    DeviceIdentificationResult? Map(JsonElement body, string serviceTag);
}

/// <summary>
/// **Not yet implemented, deliberately, and this is the last piece of the Dell integration.**
/// <para>
/// Dell's field names are documented inside the SDK issued after API approval, and the public
/// sources disagree about both the endpoint and the response — one gives
/// <c>apigtwb2c.us.dell.com/PROD/sbil/eapi/v5/asset-entitlements</c>, another an
/// <c>api.dell.com/support/v2</c> path it then hedges on. Writing a mapper against remembered or
/// blog-sourced field names would produce a class that looks finished, compiles, passes a test
/// written from the same guess, and quietly returns nothing — or worse, the wrong model — against
/// the real API.
/// </para>
/// <para>
/// So it returns null until one real response is available to write it against. The provider treats
/// null as "not identified", which is the same path a device Dell does not know takes: nothing is
/// claimed, nothing is cached, and the technician fills the form in by hand.
/// </para>
/// </summary>
public sealed class DellEntitlementMapper : IDellEntitlementMapper
{
    public DeviceIdentificationResult? Map(JsonElement body, string serviceTag) => null;
}
