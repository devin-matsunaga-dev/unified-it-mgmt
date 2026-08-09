using System.IO;

using Aspire.Hosting.ApplicationModel;

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
var bindHost = string.Equals(publicHostValue, "localhost", StringComparison.OrdinalIgnoreCase)
    ? "localhost"
    : "0.0.0.0";

var keycloakAuthority = ReferenceExpression.Create($"http://{publicHost}:8080/realms/it-platform");
var apiBaseUrl = ReferenceExpression.Create($"http://{publicHost}:5000");
var webOrigin = ReferenceExpression.Create($"http://{publicHost}:5173");

var postgres = builder.AddPostgres("postgres")
    .WithImage("timescale/timescaledb-ha", "pg17")
    .WithDataVolume();
var database = postgres.AddDatabase("database", "it_platform");

var redis = builder.AddRedis("redis")
    .WithDataVolume();

var rabbitMqUsername = builder.AddParameter("rabbitmq-username", "itplatform");
var rabbitMqPassword = AddGeneratedPassword(builder, "rabbitmq-password");
var rabbitMq = builder.AddRabbitMQ("rabbitmq", rabbitMqUsername, rabbitMqPassword)
    .WithDataVolume("it-platform-rabbitmq-data-v2")
    .WithManagementPlugin();

// Keycloak's realm import performs its own ${...} substitution, but it resolves against neither the
// container environment nor -D system properties: a placeholder silently collapses to its default,
// which is how the LAN redirect URI went missing while looking configured. Render the realm here
// instead, where the host is already known, and mount the rendered copy — the result is a plain file
// on disk that can be read back and checked.
var realmTemplatePath = Path.Combine(builder.Environment.ContentRootPath, "Keycloak", "it-platform-realm.json");
var renderedRealmPath = Path.Combine(builder.Environment.ContentRootPath, "obj", "keycloak", "it-platform-realm.json");
Directory.CreateDirectory(Path.GetDirectoryName(renderedRealmPath)!);
File.WriteAllText(
    renderedRealmPath,
    File.ReadAllText(realmTemplatePath).Replace("${PUBLIC_HOST}", publicHostValue, StringComparison.Ordinal));

var keycloakAdmin = builder.AddParameter("keycloak-admin", "admin");
var keycloakPassword = AddGeneratedPassword(builder, "keycloak-password");
var keycloak = builder.AddContainer("keycloak", "quay.io/keycloak/keycloak", "26.3")
    .WithArgs("start-dev", "--health-enabled=true", "--import-realm")
    .WithEnvironment("KC_BOOTSTRAP_ADMIN_USERNAME", keycloakAdmin)
    .WithEnvironment("KC_BOOTSTRAP_ADMIN_PASSWORD", keycloakPassword)
    .WithHttpEndpoint(port: 8080, targetPort: 8080, name: "http")
    .WithEndpoint("http", endpoint => endpoint.TargetHost = bindHost)
    .WithHttpEndpoint(targetPort: 9000, name: "management")
    .WithBindMount(
        renderedRealmPath,
        "/opt/keycloak/data/import/it-platform-realm.json",
        isReadOnly: true)
    .WithVolume("it-platform-keycloak-data", "/opt/keycloak/data")
    .WithHttpHealthCheck("/health/ready", endpointName: "management");

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

var mailhog = builder.AddContainer("mailhog", "mailhog/mailhog", "v1.0.1")
    .WithEndpoint(targetPort: 1025, name: "smtp")
    .WithHttpEndpoint(targetPort: 8025, name: "http");

var webHost = builder.AddProject<Projects.Web_Host>("web-host")
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
        endpoint.Port = 5000;
        endpoint.TargetHost = bindHost;
    })
    .WithHttpHealthCheck("/health");

builder.AddProject<Projects.Seeder>("seeder")
    .WithReference(database)
    .WaitFor(webHost);

builder.AddViteApp("web", "../../web")
    .WithReference(webHost)
    // Both are baked into the page the browser downloads, so they have to name a host that browser
    // can resolve — "localhost" on a phone is the phone.
    .WithEnvironment("VITE_API_BASE_URL", apiBaseUrl)
    .WithEnvironment("VITE_OIDC_AUTHORITY", keycloakAuthority)
    .WithEnvironment("VITE_OIDC_CLIENT_ID", "it-platform-web")
    .WithEndpoint("http", endpoint =>
    {
        endpoint.Port = 5173;
        endpoint.TargetHost = bindHost;
    })
    .WaitFor(webHost)
    .WaitFor(keycloak);

builder.Build().Run();

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
