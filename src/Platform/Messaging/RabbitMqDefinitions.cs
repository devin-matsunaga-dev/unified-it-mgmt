using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Platform.Messaging;

/// <summary>
/// One RabbitMQ account and what it is allowed to do with it. The three patterns are RabbitMQ's own
/// permission triple, and each is a regular expression matched against a resource name: an empty
/// pattern permits nothing at all, which is how "publish-only" is actually expressed.
/// </summary>
/// <param name="Username">The account name.</param>
/// <param name="Password">The plaintext password; only its salted hash is written to the file.</param>
/// <param name="Configure">Resources the account may declare or delete.</param>
/// <param name="Write">Exchanges the account may publish to.</param>
/// <param name="Read">Queues the account may consume from.</param>
/// <param name="Tags">RabbitMQ user tags; a publisher needs none.</param>
public sealed record RabbitMqAccount(
    string Username,
    string Password,
    string Configure,
    string Write,
    string Read,
    IReadOnlyList<string> Tags);

/// <summary>
/// Renders the RabbitMQ definitions document that provisions the broker's accounts, permissions and
/// the exchanges a publish-only account cannot declare for itself.
/// <para>
/// This lives in Platform rather than in AppHost because the same document is what the tests import
/// into a throwaway broker to prove the poller's credential really is publish-only. A permission
/// model asserted against a hand-written copy proves nothing about the one that ships.
/// </para>
/// </summary>
public static class RabbitMqDefinitions
{
    /// <summary>The default virtual host; Aspire's connection string uses it and nothing changes it.</summary>
    public const string VirtualHost = "/";

    /// <summary>
    /// MassTransit names a message-type exchange after the message URN with the <c>urn:message:</c>
    /// prefix removed, so <c>Contracts.Events.PollerHeartbeat</c> publishes here. The poller has no
    /// <c>configure</c> permission and therefore cannot declare it, which is why the definitions
    /// file does — whichever of the two starts first, the exchange is already there.
    /// </summary>
    public const string PollerHeartbeatExchange = "Contracts.Events:PollerHeartbeat";

    /// <summary>The permission pattern that matches nothing. RabbitMQ spells "no rights" as an empty regex.</summary>
    public const string DenyAll = "";

    /// <summary>
    /// The publish-only account: it may write to the heartbeat exchange and do nothing else. It
    /// cannot declare a queue (no <c>configure</c>) and cannot consume from one (no <c>read</c>), so
    /// a stolen poller credential cannot read a ticket, an alert, or another poller's traffic.
    /// </summary>
    public static RabbitMqAccount PublishOnlyPoller(string username, string password) => new(
        username,
        password,
        Configure: DenyAll,
        Write: $"^{PollerHeartbeatExchange.Replace(".", "\\.", StringComparison.Ordinal)}$",
        Read: DenyAll,
        Tags: []);

    /// <summary>The platform's own account: the API declares its own topology, so it keeps full rights.</summary>
    public static RabbitMqAccount Administrator(string username, string password) => new(
        username,
        password,
        Configure: ".*",
        Write: ".*",
        Read: ".*",
        Tags: ["administrator"]);

    /// <summary>
    /// Renders the definitions document. Passwords are written as salted SHA-256 hashes in
    /// RabbitMQ's own format, never in plaintext, because the rendered file sits on disk for the
    /// life of the container.
    /// </summary>
    public static string Render(IReadOnlyList<RabbitMqAccount> accounts)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        if (accounts.Count == 0)
        {
            throw new ArgumentException("At least one account is required.", nameof(accounts));
        }

        var users = new JsonArray();
        var permissions = new JsonArray();
        foreach (var account in accounts)
        {
            users.Add(new JsonObject
            {
                ["name"] = account.Username,
                ["password_hash"] = HashPassword(account.Password),
                ["hashing_algorithm"] = "rabbit_password_hashing_sha256",
                ["tags"] = new JsonArray([.. account.Tags.Select(tag => (JsonNode)tag!)]),
            });
            permissions.Add(new JsonObject
            {
                ["user"] = account.Username,
                ["vhost"] = VirtualHost,
                ["configure"] = account.Configure,
                ["write"] = account.Write,
                ["read"] = account.Read,
            });
        }

        var document = new JsonObject
        {
            ["rabbit_version"] = "4.0.0",
            ["rabbitmq_version"] = "4.0.0",
            ["users"] = users,
            ["vhosts"] = new JsonArray(new JsonObject { ["name"] = VirtualHost }),
            ["permissions"] = permissions,
            ["topic_permissions"] = new JsonArray(),
            ["parameters"] = new JsonArray(),
            ["global_parameters"] = new JsonArray(),
            ["policies"] = new JsonArray(),
            ["queues"] = new JsonArray(),
            // Declared durable and fanout to match exactly what MassTransit declares for the same
            // message type. A mismatch on either would fail the API's own declaration with
            // PRECONDITION_FAILED and take the bus down on start-up.
            ["exchanges"] = new JsonArray(new JsonObject
            {
                ["name"] = PollerHeartbeatExchange,
                ["vhost"] = VirtualHost,
                ["type"] = "fanout",
                ["durable"] = true,
                ["auto_delete"] = false,
                ["internal"] = false,
                ["arguments"] = new JsonObject(),
            }),
            ["bindings"] = new JsonArray(),
        };

        return document.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// RabbitMQ's password format: four random salt bytes, then SHA-256 over salt followed by the
    /// UTF-8 password, and the two concatenated and base64-encoded.
    /// </summary>
    private static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(4);
        var payload = new byte[salt.Length + Encoding.UTF8.GetByteCount(password)];
        salt.CopyTo(payload, 0);
        Encoding.UTF8.GetBytes(password, payload.AsSpan(salt.Length));

        var hash = SHA256.HashData(payload);
        var stored = new byte[salt.Length + hash.Length];
        salt.CopyTo(stored, 0);
        hash.CopyTo(stored, salt.Length);
        return Convert.ToBase64String(stored);
    }
}
