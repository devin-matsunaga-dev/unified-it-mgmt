using Aspire.Hosting.ApplicationModel;

var builder = DistributedApplication.CreateBuilder(args);

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

var keycloakAdmin = builder.AddParameter("keycloak-admin", "admin");
var keycloakPassword = AddGeneratedPassword(builder, "keycloak-password");
var keycloak = builder.AddContainer("keycloak", "quay.io/keycloak/keycloak", "26.3")
    .WithArgs("start-dev", "--health-enabled=true")
    .WithEnvironment("KC_BOOTSTRAP_ADMIN_USERNAME", keycloakAdmin)
    .WithEnvironment("KC_BOOTSTRAP_ADMIN_PASSWORD", keycloakPassword)
    .WithHttpEndpoint(targetPort: 8080, name: "http")
    .WithHttpEndpoint(targetPort: 9000, name: "management")
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

builder.AddProject<Projects.Web_Host>("web-host")
    .WithReference(database)
    .WithReference(redis)
    .WithReference(rabbitMq)
    .WithEnvironment("ConnectionStrings__keycloak", keycloak.GetEndpoint("http"))
    .WithEnvironment("ConnectionStrings__minio", minio.GetEndpoint("api"))
    .WaitFor(database)
    .WaitFor(redis)
    .WaitFor(rabbitMq)
    .WaitFor(keycloak)
    .WaitFor(minio)
    .WithHttpHealthCheck("/health");

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
