using Pole.AppHost.MongoDb;
using Microsoft.Extensions.Configuration;

var builder = DistributedApplication.CreateBuilder(args);
var e2eEnabled = builder.Configuration.GetValue("E2E:Enabled", false);
var e2eKeycloakPort = e2eEnabled ? builder.Configuration.GetValue<int?>("E2E:KeycloakPort") : null;

var mongoUsername = builder.AddParameter("mongo-username");
var mongoPassword = builder.AddParameter("mongo-password", secret: true);
var mongoServer = builder.AddMongoDB("pole-mongo", userName: mongoUsername, password: mongoPassword)
    .WithBindMount("MongoDb/mongo-entrypoint.sh", "/usr/local/bin/mongo-entrypoint.sh", isReadOnly: true)
    .WithEntrypoint("/usr/local/bin/mongo-entrypoint.sh")
    .WithMongoExpress(options =>
    {
        if (!e2eEnabled)
            options.WithLifetime(ContainerLifetime.Persistent);
    })
    .WithReplicaSet();
if (!e2eEnabled)
{
    mongoServer
        .WithEndpoint("tcp", endpoint =>
        {
            endpoint.Port = 27017;
            endpoint.TargetPort = 27017;
            endpoint.IsProxied = false;
        }, createIfNotExists: true)
        .WithDataVolume("pole-mongo-data")
        .WithLifetime(ContainerLifetime.Persistent);
}
else
{
    mongoServer.WithLifetime(ContainerLifetime.Session);
}

var mongoReplicaSet = builder
    .AddMongoReplicaSet("pole-database", mongoServer.Resource);

var keycloakUsername = builder.AddParameter("keycloak-username");
var keycloakPassword = builder.AddParameter("keycloak-password", secret: true);
var keycloak = builder.AddKeycloak("pole-keycloak", e2eEnabled ? null : 8080, keycloakUsername, keycloakPassword)
    .WithRealmImport("../../../infrastructure/keycloak/realms/pole.control-plane.realm.json")
    .WithEnvironment("KC_HTTP_MANAGEMENT_SCHEME", "http")
    .WithEndpoint("management", endpoint => endpoint.UriScheme = "http");
if (!e2eEnabled)
{
    keycloak
        .WithDataVolume("pole-keycloak-data")
        .WithLifetime(ContainerLifetime.Persistent);
}
else
{
    if (e2eKeycloakPort is { } e2ePort)
    {
        keycloak.WithEndpoint("http", endpoint =>
        {
            endpoint.Port = e2ePort;
            endpoint.TargetPort = 8080;
            endpoint.IsProxied = false;
        }, createIfNotExists: true);
    }

    keycloak.WithLifetime(ContainerLifetime.Session);
}

var rabbitMqUsername = builder.AddParameter("rabbitmq-username");
var rabbitMqPassword = builder.AddParameter("rabbitmq-password", secret: true);
var rabbitMq = builder.AddRabbitMQ("pole-rabbitmq", rabbitMqUsername, rabbitMqPassword)
    .WithManagementPlugin();
if (!e2eEnabled)
{
    rabbitMq
        .WithDataVolume("pole-rabbitmq-data")
        .WithLifetime(ContainerLifetime.Persistent);
}
else
{
    rabbitMq.WithLifetime(ContainerLifetime.Session);
}

var api = builder.AddProject<Projects.Pole_Server>("pole-server")
    .WithReference(keycloak)
    .WithReference(mongoServer)
    .WithReference(mongoReplicaSet)
    .WithReference(rabbitMq);
if (e2eEnabled)
{
    api
        .WaitFor(mongoReplicaSet)
        .WaitFor(rabbitMq);
}
else
{
    api
        .WaitFor(keycloak)
        .WaitFor(mongoServer)
        .WaitFor(mongoReplicaSet)
        .WaitFor(rabbitMq);
}
api.WithHttpHealthCheck("/health", endpointName: "http");

if (bool.TryParse(builder.Configuration["LocalSourceBuilds:Enabled"], out var localSourceBuildsEnabled) &&
    localSourceBuildsEnabled)
{
    var harborComposeDirectory = builder.Configuration["LocalSourceBuilds:HarborComposeDirectory"];
    if (string.IsNullOrWhiteSpace(harborComposeDirectory) ||
        !File.Exists(Path.Combine(harborComposeDirectory, "docker-compose.yml")))
    {
        throw new InvalidOperationException(
            "LocalSourceBuilds:HarborComposeDirectory must contain Harbor's generated docker-compose.yml.");
    }

    builder.AddExecutable(
            "pole-harbor",
            "docker",
            Path.GetFullPath(harborComposeDirectory),
            "compose",
            "up",
            "--remove-orphans")
        .WithExplicitStart();

    var buildKit = builder.AddContainer("pole-buildkit", "moby/buildkit", "v0.30.0")
        .WithBindMount("BuildKit/buildkitd.toml", "/etc/buildkit/buildkitd.toml", isReadOnly: true)
        .WithArgs(
            "--addr", "tcp://0.0.0.0:1234",
            "--config", "/etc/buildkit/buildkitd.toml")
        .WithContainerRuntimeArgs("--privileged")
        .WithEndpoint("grpc", endpoint =>
        {
            if (!e2eEnabled)
                endpoint.Port = 1234;
            endpoint.TargetPort = 1234;
            endpoint.IsProxied = e2eEnabled;
            endpoint.UriScheme = "tcp";
        });
    if (!e2eEnabled)
    {
        buildKit
            .WithVolume("pole-buildkit-data", "/var/lib/buildkit")
            .WithLifetime(ContainerLifetime.Persistent);
    }
    else
    {
        buildKit.WithLifetime(ContainerLifetime.Session);
    }
    buildKit.WithExplicitStart();

    var harborAdminPassword = builder.AddParameter("harbor-admin-password", secret: true);
    api.WithEnvironment("Harbor__Enabled", "true")
        .WithEnvironment(
            "Harbor__BaseUrl",
            builder.Configuration["LocalSourceBuilds:HarborBaseUrl"] ?? "http://localhost:5000")
        .WithEnvironment(
            "Harbor__RegistryHost",
            builder.Configuration["LocalSourceBuilds:HarborRegistryHost"] ?? "host.docker.internal:5000")
        .WithEnvironment("Harbor__Username", "admin")
        .WithEnvironment("Harbor__Password", harborAdminPassword)
        .WithEnvironment("Harbor__RobotDurationDays", "-1")
        .WithEnvironment("BuildKit__Enabled", "true")
        .WithEnvironment(
            "BuildKit__Address",
            buildKit.GetEndpoint("grpc"))
        .WithEnvironment(
            "BuildKit__BuildctlPath",
            builder.Configuration["LocalSourceBuilds:BuildctlPath"] ?? "buildctl")
        .WithEnvironment("BuildKit__TimeoutMinutes", "30");
}

var githubParameterNames = new[]
{
    "github-app-client-id",
    "github-app-client-secret",
    "github-app-slug",
    "github-app-private-key-path",
    "github-app-public-base-url"
};
var githubAppEnabled = githubParameterNames.All(
    name => !string.IsNullOrWhiteSpace(builder.Configuration[$"Parameters:{name}"]));
if (githubAppEnabled)
{
    var githubAppClientId = builder.AddParameter("github-app-client-id");
    var githubAppClientSecret = builder.AddParameter("github-app-client-secret", secret: true);
    var githubAppSlug = builder.AddParameter("github-app-slug");
    var githubAppPrivateKeyPath = builder.AddParameter("github-app-private-key-path", secret: true);
    var githubAppPublicBaseUrl = builder.AddParameter("github-app-public-base-url");

    api.WithEnvironment("GitHubApp__Enabled", "true")
        .WithEnvironment("GitHubApp__ClientId", githubAppClientId)
        .WithEnvironment("GitHubApp__ClientSecret", githubAppClientSecret)
        .WithEnvironment("GitHubApp__AppSlug", githubAppSlug)
        .WithEnvironment("GitHubApp__PrivateKeyPath", githubAppPrivateKeyPath)
        .WithEnvironment("GitHubApp__PublicBaseUrl", githubAppPublicBaseUrl)
        .WithEnvironment(
            "GitHubApp__ApiBaseUrl",
            builder.Configuration["GitHubApp:ApiBaseUrl"] ?? "https://api.github.com")
        .WithEnvironment(
            "GitHubApp__WebBaseUrl",
            builder.Configuration["GitHubApp:WebBaseUrl"] ?? "https://github.com");
}

var e2eWorkspaceRoot = builder.Configuration["E2E:WorkspaceRoot"];
if (e2eEnabled && !string.IsNullOrWhiteSpace(e2eWorkspaceRoot))
    api.WithEnvironment("SourceBuildExecution__WorkspaceRoot", e2eWorkspaceRoot);
var e2eKubernetesNamespacePrefix = builder.Configuration["E2E:KubernetesNamespacePrefix"];
if (e2eEnabled && !string.IsNullOrWhiteSpace(e2eKubernetesNamespacePrefix))
    api.WithEnvironment("kubernetes__NamespaceNamePrefix", e2eKubernetesNamespacePrefix);

var blog = builder.AddProject<Projects.Blog>("blog")
    .WithReference(api)
    .WaitFor(api)
    .WithExplicitStart();

var frontend = builder.AddViteApp("frontend", "../frontend")
    .WithEndpoint("http", (endpointAnnotation) =>
    {
        endpointAnnotation.Port = e2eEnabled ? null : 5173;
    })
    .WithReference(api)
    .WaitFor(api)
    .WithEnvironment("VITE_API_BASE_URL", api.GetEndpoint("http"))
    .WithEnvironment("VITE_KEYCLOAK_URL", keycloak.GetEndpoint("http"))
    .WithEnvironment("VITE_KEYCLOAK_REALM", "pole.control-plane")
    .WithEnvironment("VITE_KEYCLOAK_CLIENT_ID", "pole.control-plane.ui")
    .WithHttpHealthCheck("/", endpointName: "http")
    .WithExternalHttpEndpoints();

api.WithEnvironment("Cors__FrontendOrigin", frontend.GetEndpoint("http"));
api.WithEnvironment("KeycloakAdmin__BaseUrl", keycloak.GetEndpoint("http"));
if (e2eKeycloakPort is { } keycloakPort)
{
    var keycloakIssuer = "http://localhost:" + keycloakPort + "/realms/pole.control-plane";
    api.WithEnvironment("Authentication__ValidIssuer", keycloakIssuer);
}
if (githubAppEnabled)
{
    api.WithEnvironment("GitHubApp__FrontendBaseUrl", frontend.GetEndpoint("http"));
    if (e2eEnabled)
        api.WithEnvironment("GitHubApp__PublicBaseUrl", api.GetEndpoint("http"));
}

builder.Build().Run();
