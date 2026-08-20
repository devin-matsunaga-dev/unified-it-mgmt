using System.IO;

using Aspire.Hosting.ApplicationModel;

using Platform.Messaging;

var builder = DistributedApplication.CreateBuilder(args);

// The host name every *client* uses to reach this stack. It defaults to localhost, which is right
// for a browser on the same machine; set it to the machine's LAN address
// (`env 'Parameters__public-host=192.168.0.2' aspire run` — `env`, because the hyphen makes it an
// illegal shell identifier and a bare prefix assignment is refused) when something off-box has to connect — a phone
// scanning a printed asset label, for instance. Keycloak stamps its token issuer from the host it
// was called on and the API demands an exact match, so browser, API and realm must all agree on one
// name; that is also why the ports below are pinned rather than left to Aspire to allocate. A
// reference expression can only interpolate resources, so those port numbers appear both here and on
// the endpoints, and have to be kept in step by hand.
// Read out of configuration rather than left to AddParameter's default, because that overload takes
// a *given* value and the host name has to be known here as a plain string as well — see bindHost.
// `env 'Parameters__public-host=192.168.0.2' aspire run` is the environment form of this key; it
// needs `env` because a hyphenated name cannot be assigned through the shell.
var publicHostValue = builder.Configuration["Parameters:public-host"] is { Length: > 0 } configured
    ? configured
    : "localhost";
var publicHost = builder.AddParameter("public-host", publicHostValue);

// Naming the LAN host is not enough on its own: Aspire's proxy binds to localhost, so the pinned
// ports below are loopback-only and a phone gets a refused connection. Binding to every interface is
// therefore tied to the same parameter rather than applied unconditionally — a default `aspire run`
// stays exactly as private as it was, and only a run that has already declared itself LAN-facing
// opens the three endpoints a browser off this box has to reach.
var isLanRun = !string.Equals(publicHostValue, "localhost", StringComparison.OrdinalIgnoreCase);
var bindHost = isLanRun ? "0.0.0.0" : "localhost";

// A LAN run is served over TLS, and not for the usual reason. `oidc-client-ts` builds its PKCE
// challenge with `crypto.subtle`, which the browser only exposes in a secure context, and the
// library hardcodes S256 — so on plain HTTP a phone loads the SPA and can never sign in, with no
// weaker-PKCE fallback to reach for. That makes HTTPS the price of the printed-label journey
// working at all. Three origins need it: the SPA, because it is the secure context; the API,
// because an HTTPS page may not fetch an HTTP one; and Keycloak, because the code-for-token
// exchange is a fetch from that same page. All three present the one leaf certificate written by
// `scripts/dev-certs.sh`, which carries the LAN address and localhost as subject alternative
// names and chains to a local CA installed once on the phone.
// Everything here is gated on the same parameter the LAN binding is: a plain `aspire run` keeps
// its HTTP endpoints, its ports and its realm exactly as they were.
var certDir = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", "..", "certs"));
var serverCertPath = Path.Combine(certDir, "dev-server.crt");
var serverKeyPath = Path.Combine(certDir, "dev-server.key");
// What the API trusts when it calls Keycloak by its public name. .NET on Linux validates through
// OpenSSL, which reads SSL_CERT_FILE — so the CA is handed to the one process that needs it
// rather than installed machine-wide.
var caBundlePath = Path.Combine(certDir, "dev-ca-bundle.crt");
if (isLanRun && !(File.Exists(serverCertPath) && File.Exists(serverKeyPath) && File.Exists(caBundlePath)))
{
    throw new InvalidOperationException(
        $"A LAN run serves TLS, and no certificate was found in '{certDir}'. "
        + $"Run scripts/dev-certs.sh {publicHostValue} first.");
}

// The scheme is fixed at this point rather than interpolated, because a reference expression can
// only interpolate resources — a plain string has to be part of the literal.
var keycloakAuthority = isLanRun
    ? ReferenceExpression.Create($"https://{publicHost}:8443/realms/it-platform")
    : ReferenceExpression.Create($"http://{publicHost}:8080/realms/it-platform");
var apiBaseUrl = isLanRun
    ? ReferenceExpression.Create($"https://{publicHost}:5000")
    : ReferenceExpression.Create($"http://{publicHost}:5000");
// The SPA's port, and the one thing a person types. 5173 by default; set to 443 to drop it from the
// address bar entirely — `env 'Parameters__web-port=443'`. Opt-in rather than automatic because 443
// is privileged: binding it needs `net.ipv4.ip_unprivileged_port_start` lowered, and a run that
// assumed it would fail to start on any machine without that.
var webPortValue = int.TryParse(builder.Configuration["Parameters:web-port"], out var configuredPort)
    && configuredPort is > 0 and < 65536
        ? configuredPort
        : 5173;
// Omitted when it is the scheme's own default, because "https://host:443" is the same origin as
// "https://host" to a browser but a different string everywhere it is compared — the realm's
// redirect URIs and the API's CORS origin among them.
var webPortSuffix = (isLanRun, webPortValue) switch
{
    (true, 443) => string.Empty,
    (false, 80) => string.Empty,
    _ => $":{webPortValue}",
};
var webOrigin = isLanRun
    ? ReferenceExpression.Create($"https://{publicHost}{webPortSuffix}")
    : ReferenceExpression.Create($"http://{publicHost}{webPortSuffix}");
// The same two origins as plain strings, for the realm document, which is rendered on disk here
// rather than resolved by Aspire. The localhost pair is kept so that a LAN run is still usable
// from a browser on this machine, whose scheme has moved with everything else.
var webOriginValue = isLanRun
    ? $"https://{publicHostValue}{webPortSuffix}"
    : $"http://{publicHostValue}{webPortSuffix}";
var localOriginValue = isLanRun
    ? $"https://localhost{webPortSuffix}"
    : $"http://localhost{webPortSuffix}";

var postgres = builder.AddPostgres("postgres")
    .WithImage("timescale/timescaledb-ha", "pg17")
    .WithDataVolume();
var database = postgres.AddDatabase("database", "it_platform");

var redis = builder.AddRedis("redis")
    .WithDataVolume();

var rabbitMqUsername = builder.AddParameter("rabbitmq-username", "itplatform");
var rabbitMqPassword = AddGeneratedPassword(builder, "rabbitmq-password");

// The poller is the first thing on this bus that is not the platform itself, so it gets an account
// of its own rather than the API's. Its rights are write-only, on a closed list of exchanges:
// RabbitMQ has no "publish-only" flag, so it is expressed as an empty configure pattern, an empty
// read pattern, and a write pattern anchored to exactly the heartbeat and telemetry exchanges
// (`RabbitMqDefinitions.PollerExchanges`). Because it cannot declare them either, the definitions
// file below does.
var pollerBusUsername = builder.AddParameter("poller-bus-username", "poller");
var pollerBusPassword = AddGeneratedPassword(builder, "poller-bus-password");

// The discovery service (WP-4.1) gets its own account rather than sharing the poller's, so the two
// write patterns stay disjoint: a scanner that could publish telemetry could report a measurement of a
// device it has never polled. Its list is one exchange long.
var discoveryBusUsername = builder.AddParameter("discovery-bus-username", "discovery");
var discoveryBusPassword = AddGeneratedPassword(builder, "discovery-bus-password");

// Rendered here for the same reason the Keycloak realm is: the values are only known at this point,
// and a file on disk can be read back and checked. The renderer lives in Platform so the tests
// import this exact document into a throwaway broker — a permission model proved against a
// hand-written copy proves nothing about the one that ships.
var rabbitMqDefinitionsPath = Path.Combine(builder.Environment.ContentRootPath, "obj", "rabbitmq", "definitions.json");
var rabbitMqDefinitionsConfPath = Path.Combine(builder.Environment.ContentRootPath, "obj", "rabbitmq", "20-definitions.conf");
Directory.CreateDirectory(Path.GetDirectoryName(rabbitMqDefinitionsPath)!);
File.WriteAllText(
    rabbitMqDefinitionsPath,
    RabbitMqDefinitions.Render(
    [
        RabbitMqDefinitions.Administrator(
            await ValueOf(rabbitMqUsername), await ValueOf(rabbitMqPassword)),
        RabbitMqDefinitions.PublishOnlyPoller(
            await ValueOf(pollerBusUsername), await ValueOf(pollerBusPassword)),
        RabbitMqDefinitions.PublishOnlyDiscovery(
            await ValueOf(discoveryBusUsername), await ValueOf(discoveryBusPassword)),
    ]));
// conf.d rather than rabbitmq.conf: the image's entrypoint writes the default-user file into the
// same directory from RABBITMQ_DEFAULT_USER, and replacing the main file would throw that away.
File.WriteAllText(rabbitMqDefinitionsConfPath, "load_definitions = /etc/rabbitmq/definitions.json\n");

var rabbitMq = builder.AddRabbitMQ("rabbitmq", rabbitMqUsername, rabbitMqPassword)
    .WithDataVolume("it-platform-rabbitmq-data-v2")
    .WithManagementPlugin()
    .WithBindMount(rabbitMqDefinitionsPath, "/etc/rabbitmq/definitions.json", isReadOnly: true)
    .WithBindMount(rabbitMqDefinitionsConfPath, "/etc/rabbitmq/conf.d/20-definitions.conf", isReadOnly: true);

// Keycloak's realm import performs its own ${...} substitution, but it resolves against neither the
// container environment nor -D system properties: a placeholder silently collapses to its default,
// which is how the LAN redirect URI went missing while looking configured. Render the realm here
// instead, where the host is already known, and mount the rendered copy — the result is a plain file
// on disk that can be read back and checked.
// The poller's client secret is rendered into the realm the same way, and for the same reason it is
// a generated persisted parameter rather than a literal in the template: a credential checked into
// the repository is a credential everyone has.
var pollerClientSecret = AddGeneratedPassword(builder, "poller-client-secret");
var discoveryClientSecret = AddGeneratedPassword(builder, "discovery-client-secret");

var realmTemplatePath = Path.Combine(builder.Environment.ContentRootPath, "Keycloak", "it-platform-realm.json");
var renderedRealmPath = Path.Combine(builder.Environment.ContentRootPath, "obj", "keycloak", "it-platform-realm.json");
Directory.CreateDirectory(Path.GetDirectoryName(renderedRealmPath)!);
File.WriteAllText(
    renderedRealmPath,
    File.ReadAllText(realmTemplatePath)
        .Replace("${WEB_ORIGIN}", webOriginValue, StringComparison.Ordinal)
        .Replace("${LOCAL_ORIGIN}", localOriginValue, StringComparison.Ordinal)
        .Replace("${POLLER_CLIENT_SECRET}", await ValueOf(pollerClientSecret), StringComparison.Ordinal)
        .Replace("${DISCOVERY_CLIENT_SECRET}", await ValueOf(discoveryClientSecret), StringComparison.Ordinal));

var keycloakAdmin = builder.AddParameter("keycloak-admin", "admin");
var keycloakPassword = AddGeneratedPassword(builder, "keycloak-password");
var keycloak = builder.AddContainer("keycloak", "quay.io/keycloak/keycloak", "26.3")
    .WithArgs("start-dev", "--health-enabled=true", "--import-realm")
    // Pin the issuer instead of letting Keycloak infer it from whoever asked. Until WP-3.2 every
    // client was a browser or the API, and both call Keycloak by the same name; the poller is the
    // first client inside a container, which must dial host.docker.internal and would otherwise be
    // handed a token stamped with an issuer the API rejects. One name, whatever the route.
    .WithEnvironment("KC_HOSTNAME", isLanRun
        ? ReferenceExpression.Create($"https://{publicHost}:8443")
        : ReferenceExpression.Create($"http://{publicHost}:8080"))
    .WithEnvironment("KC_BOOTSTRAP_ADMIN_USERNAME", keycloakAdmin)
    .WithEnvironment("KC_BOOTSTRAP_ADMIN_PASSWORD", keycloakPassword)
    .WithHttpEndpoint(port: 8080, targetPort: 8080, name: "http")
    .WithEndpoint("http", endpoint => endpoint.TargetHost = bindHost)
    .WithHttpEndpoint(targetPort: 9000, name: "management")
    .WithBindMount(
        renderedRealmPath,
        "/opt/keycloak/data/import/it-platform-realm.json",
        isReadOnly: true)
    // Deliberately no data volume, which is a change WP-4.1 had to make to be verifiable at all.
    // `--import-realm` imports with strategy IGNORE_EXISTING: with the realm persisted in a volume,
    // Keycloak logs "Realm 'it-platform' already exists. Import skipped" and every later edit to
    // it-platform-realm.json is silently ignored. WP-4.1's `Discovery` role and
    // `it-platform-discovery` client simply were not there on a machine that had run the stack
    // before, and the only symptom was the scanner failing to get a token — nothing said why.
    // A fresh Keycloak per run re-imports the realm every time, which is the same clean-slate call
    // WP-1.9 made for the Postgres volume and for the same reason: everything here is rendered from
    // the repository, so there is nothing in Keycloak worth keeping that is not also in git. The
    // cost is that a hand-made realm change does not survive a restart, which is the point.
    // A LAN run cannot use this probe. Keycloak's management interface inherits TLS from the main
    // HTTPS certificate — it says so on start-up, "Management interface listening on
    // https://0.0.0.0:9000" — and 26.3 has no option to hold it on HTTP: the only related setting,
    // https-management-certificate-file, documents itself as inherited from the HTTP options when
    // absent. An HTTP probe of an HTTPS port never answers, so the resource stays unhealthy and
    // every resource that waits on Keycloak — the API, and through it the SPA, the poller, the
    // scanner and the seeder — never starts at all.
    // The realm document on the plain HTTP listener stands in for it, and is the better signal
    // anyway: it is served only once the realm has been imported, which is the thing everything
    // downstream is actually waiting for, where /health/ready only says the process is up.
    .WithHttpHealthCheck(
        isLanRun ? "/realms/it-platform" : "/health/ready",
        endpointName: isLanRun ? "http" : "management");

// The HTTPS listener a LAN run adds, on 8443 beside the HTTP one rather than replacing it. The
// browser and the issuer move to it; the poller, the discovery scanner and the API's Keycloak
// connection string keep dialling 8080 in the clear, which is what saves those containers from
// needing the CA in a trust store of their own. KC_HOSTNAME above is what makes the two agree on
// one issuer whichever listener answers.
if (isLanRun)
{
    keycloak
        .WithEnvironment("KC_HTTPS_CERTIFICATE_FILE", "/etc/x509/https/tls.crt")
        .WithEnvironment("KC_HTTPS_CERTIFICATE_KEY_FILE", "/etc/x509/https/tls.key")
        .WithBindMount(serverCertPath, "/etc/x509/https/tls.crt", isReadOnly: true)
        .WithBindMount(serverKeyPath, "/etc/x509/https/tls.key", isReadOnly: true)
        .WithHttpsEndpoint(port: 8443, targetPort: 8443, name: "https")
        .WithEndpoint("https", endpoint => endpoint.TargetHost = bindHost);
}

var minioAccessKey = builder.AddParameter("minio-access-key", "minioadmin");
var minioSecretKey = AddGeneratedPassword(builder, "minio-secret-key");
var minio = builder.AddContainer("minio", "quay.io/minio/minio", "RELEASE.2025-09-07T16-13-09Z")
    .WithArgs("server", "/data", "--console-address", ":9001")
    .WithEnvironment("MINIO_ROOT_USER", minioAccessKey)
    .WithEnvironment("MINIO_ROOT_PASSWORD", minioSecretKey)
    .WithHttpEndpoint(targetPort: 9000, name: "api")
    .WithHttpEndpoint(targetPort: 9001, name: "console")
    .WithVolume("it-platform-minio-data", "/data")
    .WithHttpHealthCheck("/minio/health/live", endpointName: "api");

var inboundMail = builder.AddContainer("inbound-mail", "greenmail/standalone", "2.1.11")
    .WithEnvironment(
        "GREENMAIL_OPTS",
        "-Dgreenmail.setup.test.all -Dgreenmail.hostname=0.0.0.0 " +
        "-Dgreenmail.users=helpdesk:helpdesk@it-platform.local -Dgreenmail.users.login=email")
    .WithEndpoint(targetPort: 3025, name: "smtp")
    .WithEndpoint(targetPort: 3143, name: "imap")
    .WithHttpEndpoint(targetPort: 8080, name: "api");

// MailHog is also what WP-3.8's seeded service checks point at: it is the one resource in the stack
// that answers both a plain TCP connect and an HTTP request. As with snmpsim, the poller reaches it
// by container name on the session network, not through a published endpoint.
const string MailHogHost = "mailhog";
const int MailHogSmtpPort = 1025;
const int MailHogHttpPort = 8025;
var mailhog = builder.AddContainer(MailHogHost, "mailhog/mailhog", "v1.0.1")
    .WithEndpoint(targetPort: MailHogSmtpPort, name: "smtp")
    .WithHttpEndpoint(targetPort: MailHogHttpPort, name: "http");

// The `https` launch profile publishes both an HTTPS and an HTTP URL; the `http` one publishes
// only HTTP. Which profile is used decides which endpoints exist at all, so it is chosen here
// rather than patched afterwards.
var webHost = builder.AddProject<Projects.Web_Host>("web-host", isLanRun ? "https" : "http")
    .WithReference(database)
    .WithReference(redis)
    .WithReference(rabbitMq)
    .WithEnvironment("ConnectionStrings__keycloak", keycloak.GetEndpoint("http"))
    // The public authority, not the endpoint reference: the issuer in a token minted for the browser
    // is whatever host the browser called, and this is the value that has to match it.
    .WithEnvironment("Authentication__Authority", keycloakAuthority)
    .WithEnvironment("WebClient__Origin", webOrigin)
    // What a printed QR points at. A sticker outlives the process, so this is the one setting worth
    // getting right before printing anything.
    .WithEnvironment("Assets__Labels__PublicBaseUrl", webOrigin)
    // Where a Teams or Slack deep link points. Same rule as the label URL above: the link is read in
    // a chat client that has no idea which host wrote it, so it has to be absolute and public.
    .WithEnvironment("Notifications__DeepLinkBaseUrl", webOrigin)
    .WithEnvironment("ConnectionStrings__minio", minio.GetEndpoint("api"))
    .WithEnvironment("ObjectStorage__AccessKey", minioAccessKey)
    .WithEnvironment("ObjectStorage__SecretKey", minioSecretKey)
    .WithEnvironment("Email__Smtp__Enabled", "true")
    .WithEnvironment("Email__Smtp__Host", mailhog.GetEndpoint("smtp").Property(EndpointProperty.Host))
    .WithEnvironment("Email__Smtp__Port", mailhog.GetEndpoint("smtp").Property(EndpointProperty.Port))
    .WithEnvironment("Email__Imap__Enabled", "true")
    .WithEnvironment("Email__Imap__Host", inboundMail.GetEndpoint("imap").Property(EndpointProperty.Host))
    .WithEnvironment("Email__Imap__Port", inboundMail.GetEndpoint("imap").Property(EndpointProperty.Port))
    .WithEnvironment("Email__Imap__Username", "helpdesk@it-platform.local")
    .WithEnvironment("Email__Imap__Password", "helpdesk")
    .WaitFor(database)
    .WaitFor(redis)
    .WaitFor(rabbitMq)
    .WaitFor(keycloak)
    .WaitFor(minio)
    .WaitFor(inboundMail)
    .WaitFor(mailhog)
    .WithEndpoint("http", endpoint =>
    {
        // 5000 is the browser's port, and in a LAN run the browser is on the HTTPS endpoint
        // below. This one keeps serving the poller, the scanner and the seeder — all of which
        // resolve it through Aspire and neither know nor care which port it landed on.
        // Pinned in both modes, and in a LAN run this is load-bearing rather than tidy. Left to
        // Aspire, DCP picks a port and then tries to bind it across every address 0.0.0.0 expands
        // to — 127.0.0.1, the WSL loopback and the LAN address. When it cannot it logs "Could not
        // use the same port for all addresses" and keeps a listener on each anyway, but only some
        // of them forward. The dead one is the loopback, which is exactly the address the health
        // check resolves, so the API answers 200 on every real address and still reports unhealthy
        // — and `web` waits on that health, so the SPA never starts and 5173 refuses.
        endpoint.Port = isLanRun ? 5001 : 5000;
        endpoint.TargetHost = bindHost;
    })
    // Named rather than left to the default, which is what a LAN run turns into a trap. The `https`
    // launch profile lists its HTTPS URL first, so the default probe picks that endpoint and the
    // AppHost's own client — which has no reason to know about a certificate authority invented for
    // this machine — rejects it with PartialChain. The API answers 200 on both listeners the whole
    // time and still never reports healthy. Probing the HTTP endpoint sidesteps the question: it
    // exists in both modes, it is loopback-or-LAN local either way, and it is already the route the
    // poller, the scanner and the seeder take.
    .WithHttpHealthCheck("/health", endpointName: "http");

if (isLanRun)
{
    webHost
        .WithEndpoint("https", endpoint =>
        {
            endpoint.Port = 5000;
            endpoint.TargetHost = bindHost;
        })
        // PEM certificate and key, which Kestrel reads directly — no PFX, no password, and the
        // same pair every other origin here presents.
        .WithEnvironment("ASPNETCORE_Kestrel__Certificates__Default__Path", serverCertPath)
        .WithEnvironment("ASPNETCORE_Kestrel__Certificates__Default__KeyPath", serverKeyPath)
        // Read when the JWT handler fetches the realm's metadata and signing keys over HTTPS.
        .WithEnvironment("SSL_CERT_FILE", caBundlePath);
}

// The devices the poller polls. One container answers as several devices: snmpsim serves a different
// recording per community string, so "healthy" and "degraded" are one process and one port. Stopping
// it is how "the target goes away → a down event" is verified by hand.
// Deliberately no published endpoint. The only thing that polls it is the poller, which is a
// container on the same Aspire session network, so it reaches "snmpsim" by name on 161 directly.
// Publishing a host port instead sends the traffic through DCP's proxy, which binds loopback unless
// told otherwise (the WP-2.7 trap) — reachable from the host and not from the container that needs
// it, which is exactly how the first live walk of this package found every SNMP check timing out.
//
// WP-4.5 gave the healthy profile an interface table, and it uses two snmpsim variation modules that
// were probed against this image rather than assumed. `numeric` makes the octet counters climb at a
// configured rate, which is what a traffic rate is derived from — a static recording would report
// every port carrying exactly nothing forever. `writecache` makes ifOperStatus on port 2 accept an
// SNMP SET, which is how a port is taken down by hand without taking the device down; the write
// lives in the simulator's memory, so restarting the container brings every port back up.
// `src/AppHost/snmpsim/set-if-oper-status.py` is that SET, and it runs inside the poller's container
// because this one deliberately publishes no port for anything on the host to aim at.
const string SnmpSimHost = "snmpsim";
const int SnmpSimPort = 161;
var snmpSimDataPath = Path.Combine(builder.Environment.ContentRootPath, "snmpsim");
var snmpSim = builder.AddContainer(SnmpSimHost, "tandrup/snmpsim")
    .WithBindMount(snmpSimDataPath, "/usr/local/snmpsim/data", isReadOnly: true);

// The down-able device (WP-3.12), and the reason it is a second container rather than a third
// community on the one above: stopping `snmpsim` stops every simulated device at once, so the Phase 3
// demo — take one device down, watch its ticket open, revive it, watch the ticket resolve — could not
// be performed without also taking down the healthy and degraded devices the rest of the estate is
// watching. `docker stop snmpsim-downable` takes exactly one device away.
//
// Its profile is a distinct simulated switch and is deliberately filed under the community `healthy`:
// snmpsim takes the community from the file name, so reusing it means this device authenticates with
// the vaulted credential WP-3.11 already seeds, rather than needing a third secret in the vault. The
// container decides *which* device answers; the community decides which profile within it.
//
// Two mechanisms that look easier were probed against the image and rejected. The `delay` variation
// module blocks the whole responder — a request delayed past a check's timeout stalls every other
// community too, measured at 5s on a healthy read taken during one — and the `error` module answers
// immediately, so it is a device returning an error rather than a device that is not there.
const string DownableSnmpSimHost = "snmpsim-downable";
var downableSnmpSim = builder.AddContainer(DownableSnmpSimHost, "tandrup/snmpsim")
    .WithBindMount(
        Path.Combine(builder.Environment.ContentRootPath, "snmpsim-downable"),
        "/usr/local/snmpsim/data",
        isReadOnly: true);

// The mock HTTP target (WP-3.12). The seeded service checks pointed at MailHog, which was chosen in
// WP-3.8 as the one resource in the stack answering both a TCP connect and an HTTP request — but its
// page belongs to MailHog, so a content expectation set against it rots on a version bump and there
// is no honest way to break one by hand. This serves a page this repository owns: editing
// `http-target/index.html` breaks the seeded check's `expectedContent` with no restart, and stopping
// the container takes the service down. MailHog keeps its own seeded device and goes back to being
// mail.
const string HttpTargetHost = "http-target";
const int HttpTargetPort = 80;
var httpTarget = builder.AddContainer(HttpTargetHost, "nginx", "1.29-alpine")
    .WithBindMount(
        Path.Combine(builder.Environment.ContentRootPath, "http-target"),
        "/usr/share/nginx/html",
        isReadOnly: true);

// A container rather than a Python executable resource, because "stop the poller and watch the
// platform notice" is a verification step, and `docker stop` is how that is done.
builder.AddDockerfile("poller", "../../services/poller")
    // ICMP needs either a raw socket or an ICMP datagram socket, and this is the second.
    // `--cap-add=NET_RAW` looks like the obvious answer and does not work: Docker puts an added
    // capability in the permitted set, and a container running as a non-root user (this one is uid
    // 10001, deliberately) gets an empty effective set unless the binary carries a file capability.
    // Probed both ways — raw sockets fail with "Root privileges are required" under `--cap-add`
    // alone. Granting one uid the right to open a ping socket is narrower than NET_RAW anyway, and
    // it needs nothing set on the image.
    // The range must match the Dockerfile's uid; nothing but a failing ping says so if it drifts.
    .WithContainerRuntimeArgs("--sysctl", "net.ipv4.ping_group_range=10001 10001")
    .WithEnvironment("POLLER_ICMP_PRIVILEGED", "false")
    .WithEnvironment("POLLER_NAME", "poller-1")
    .WithEnvironment("POLLER_GROUP", "default")
    .WithEnvironment("POLLER_AGENT_VERSION", "0.1.0")
    .WithEnvironment("POLLER_INTERVAL_SECONDS", "15")
    // WP-5.6. The poller runs as a non-root user in a container with no init system, so the real
    // template — `systemctl restart {service}` — cannot work here and would make every seeded
    // remediation a failure. This stands in for it exactly as `snmpsim` stands in for a switch: the
    // dispatch, the argv construction, the timeout, the result on the ticket and the audit row are
    // all the real ones, and only the thing being restarted is simulated. The unit name still comes
    // from the runbook's validated parameter, so a value that would be unsafe is refused here too.
    .WithEnvironment(
        "POLLER_RUNBOOK_RESTART_SERVICE_COMMAND",
        "/bin/echo Restarted {service} (simulated: this poller has no init system).")
    .WithEnvironment("POLLER_API_BASE_URL", webHost.GetEndpoint("http"))
    // The public authority, not the container's route to it: the token's issuer is stamped from
    // KC_HOSTNAME above, and this is the address the poller dials to ask for one.
    .WithEnvironment("POLLER_OIDC_TOKEN_URL", ReferenceExpression.Create(
        $"{keycloak.GetEndpoint("http")}/realms/it-platform/protocol/openid-connect/token"))
    .WithEnvironment("POLLER_OIDC_CLIENT_ID", "it-platform-poller")
    .WithEnvironment("POLLER_OIDC_CLIENT_SECRET", pollerClientSecret)
    .WithEnvironment("POLLER_AMQP_URL", ReferenceExpression.Create(
        $"amqp://{pollerBusUsername}:{pollerBusPassword}@" +
        $"{rabbitMq.GetEndpoint("tcp").Property(EndpointProperty.Host)}:" +
        $"{rabbitMq.GetEndpoint("tcp").Property(EndpointProperty.Port)}/"))
    .WaitFor(webHost)
    .WaitFor(rabbitMq)
    .WaitFor(keycloak)
    .WaitFor(snmpSim)
    .WaitFor(downableSnmpSim)
    // The seeded service checks poll these, so a poller that started first would report the mail
    // service and the portal down for its first cycles and raise an alert about the stack starting up.
    .WaitFor(mailhog)
    .WaitFor(httpTarget);

// The discovery service (WP-4.1). A container for the same reason the poller is one, and on the same
// ICMP arrangement: a subnet sweep is thousands of pings, and `--cap-add=NET_RAW` does not give a
// non-root process an effective capability. The uid must match the Dockerfile's.
//
// It sits on the Aspire session network, which is the whole reason the seeded profile scans the
// keyword `local` rather than a literal CIDR: Docker allocates that network's subnet at session start,
// so a hardcoded range would scan an address space nothing in this stack is on. What it finds is every
// other container in the run — the simulators, the broker, the API — which is a real network scan of a
// real network, not a fixture pretending to be one.
builder.AddDockerfile("discovery", "../../services/discovery")
    .WithContainerRuntimeArgs("--sysctl", "net.ipv4.ping_group_range=10001 10001")
    .WithEnvironment("DISCOVERY_ICMP_PRIVILEGED", "false")
    .WithEnvironment("DISCOVERY_NAME", "discovery-1")
    .WithEnvironment("DISCOVERY_GROUP", "default")
    .WithEnvironment("DISCOVERY_AGENT_VERSION", "0.1.0")
    // How often it wakes to see what is due, not how often it scans: each profile carries its own
    // interval, exactly as a check does.
    .WithEnvironment("DISCOVERY_INTERVAL_SECONDS", "30")
    .WithEnvironment("DISCOVERY_API_BASE_URL", webHost.GetEndpoint("http"))
    .WithEnvironment("DISCOVERY_OIDC_TOKEN_URL", ReferenceExpression.Create(
        $"{keycloak.GetEndpoint("http")}/realms/it-platform/protocol/openid-connect/token"))
    .WithEnvironment("DISCOVERY_OIDC_CLIENT_ID", "it-platform-discovery")
    .WithEnvironment("DISCOVERY_OIDC_CLIENT_SECRET", discoveryClientSecret)
    .WithEnvironment("DISCOVERY_AMQP_URL", ReferenceExpression.Create(
        $"amqp://{discoveryBusUsername}:{discoveryBusPassword}@" +
        $"{rabbitMq.GetEndpoint("tcp").Property(EndpointProperty.Host)}:" +
        $"{rabbitMq.GetEndpoint("tcp").Property(EndpointProperty.Port)}/"))
    // Which communities an SNMP identify tries, in order. These are the simulator's two profiles: a
    // scan meets devices nobody has configured yet, so there is no check to hang a vault credential on
    // and the scanner's own configuration is where the list lives. Anything found this way is
    // identified, never polled — the credential a device is *monitored* with is still the vault's.
    .WithEnvironment("DISCOVERY_SNMP_COMMUNITIES", "healthy,degraded,public")
    .WaitFor(webHost)
    .WaitFor(rabbitMq)
    .WaitFor(keycloak)
    // Not strictly required — an empty network is a valid scan — but a scanner that started first
    // would report an estate of nothing on its first pass and look broken.
    .WaitFor(snmpSim)
    .WaitFor(downableSnmpSim)
    .WaitFor(httpTarget);

builder.AddProject<Projects.Seeder>("seeder")
    .WithReference(database)
    // The seeded devices name the simulator by an address the *poller's container* can reach. That
    // is the container network, not the host: unlike Keycloak, nothing outside the session network
    // needs to talk to the simulator.
    .WithEnvironment("Monitoring__Seed__SnmpAddress", SnmpSimHost)
    .WithEnvironment(
        "Monitoring__Seed__SnmpPort",
        SnmpSimPort.ToString(System.Globalization.CultureInfo.InvariantCulture))
    .WithEnvironment("Monitoring__Seed__PollerGroup", "default")
    // The seeded TCP and HTTP checks, addressed the same way and for the same reason.
    .WithEnvironment("Monitoring__Seed__ServiceAddress", MailHogHost)
    .WithEnvironment(
        "Monitoring__Seed__ServiceTcpPort",
        MailHogSmtpPort.ToString(System.Globalization.CultureInfo.InvariantCulture))
    .WithEnvironment(
        "Monitoring__Seed__ServiceHttpUrl",
        $"http://{MailHogHost}:{MailHogHttpPort.ToString(System.Globalization.CultureInfo.InvariantCulture)}/")
    // WP-3.12's rig. The down-able simulator answers on the same port as the other one — it is the
    // same image with a different data directory — so only the host differs.
    .WithEnvironment("Monitoring__Seed__DownableSnmpAddress", DownableSnmpSimHost)
    // And the mock HTTP target, which the seeded portal device both connects to and reads.
    .WithEnvironment("Monitoring__Seed__HttpTargetAddress", HttpTargetHost)
    .WithEnvironment(
        "Monitoring__Seed__HttpTargetPort",
        HttpTargetPort.ToString(System.Globalization.CultureInfo.InvariantCulture))
    // WP-3.10. Left unset by default, which seeds the email channel and no chat channel — a
    // placeholder webhook would fail every Critical alert and look like a broken feature. Set it
    // (with Notifications__Seed__WebhookKind = Teams or Slack) to seed the chat half.
    .WithEnvironment("Notifications__Seed__WebhookUrl",
        builder.Configuration["Notifications:Seed:WebhookUrl"] ?? string.Empty)
    .WithEnvironment("Notifications__Seed__WebhookKind",
        builder.Configuration["Notifications:Seed:WebhookKind"] ?? "Teams")
    .WaitFor(webHost)
    .WaitFor(snmpSim)
    .WaitFor(mailhog);

var web = builder.AddViteApp("web", "../../web")
    .WithReference(webHost)
    // Both are baked into the page the browser downloads, so they have to name a host that browser
    // can resolve — "localhost" on a phone is the phone.
    .WithEnvironment("VITE_API_BASE_URL", apiBaseUrl)
    .WithEnvironment("VITE_OIDC_AUTHORITY", keycloakAuthority)
    .WithEnvironment("VITE_OIDC_CLIENT_ID", "it-platform-web")
    .WithEndpoint("http", endpoint =>
    {
        endpoint.Port = webPortValue;
        endpoint.TargetHost = bindHost;
        if (isLanRun)
        {
            // The endpoint Aspire creates for a Vite app is HTTP by name; a LAN run serves TLS on
            // it, and the scheme has to say so or the dashboard and the QR payload disagree about
            // what the SPA is reachable as.
            endpoint.UriScheme = "https";
        }
    })
    .WaitFor(webHost)
    .WaitFor(keycloak);

if (isLanRun)
{
    web
        // Read by vite.config.ts, which turns on its own HTTPS when both are present. Not
        // VITE_-prefixed on purpose: these are paths on this machine and have no business being
        // baked into the bundle the browser downloads.
        .WithEnvironment("DEV_TLS_CERT_FILE", serverCertPath)
        .WithEnvironment("DEV_TLS_KEY_FILE", serverKeyPath);
}

builder.Build().Run();

// Most parameters are passed to resources by reference and resolved by Aspire. These few have to be
// read here as plain strings, because they are rendered into files — the realm import and the broker
// definitions — before any resource starts.
static async Task<string> ValueOf(IResourceBuilder<ParameterResource> parameter) =>
    await parameter.Resource.GetValueAsync(CancellationToken.None)
    ?? throw new InvalidOperationException($"Parameter '{parameter.Resource.Name}' has no value.");

static IResourceBuilder<ParameterResource> AddGeneratedPassword(
    IDistributedApplicationBuilder builder,
    string name) =>
    builder.AddParameter(
        name,
        new GenerateParameterDefault
        {
            MinLength = 24,
            Lower = true,
            Upper = true,
            Numeric = true,
            Special = false,
            MinLower = 1,
            MinUpper = 1,
            MinNumeric = 1,
        },
        secret: true,
        persist: true);
