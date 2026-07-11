using Pole.AppHost.MongoDb;

var builder = DistributedApplication.CreateBuilder(args);

var mongoUsername = builder.AddParameter("mongo-username");
var mongoPassword = builder.AddParameter("mongo-password", secret: true);
var mongoServer = builder.AddMongoDB("pole-mongo", userName: mongoUsername, password: mongoPassword)
    .WithBindMount("MongoDb/mongo-entrypoint.sh", "/usr/local/bin/mongo-entrypoint.sh", isReadOnly: true)
    .WithEntrypoint("/usr/local/bin/mongo-entrypoint.sh")
    .WithMongoExpress(options =>
    {
        options.WithLifetime(ContainerLifetime.Persistent);
    })
    .WithEndpoint("tcp", endpoint =>
    {
        endpoint.Port = 27017;
        endpoint.TargetPort = 27017;
        endpoint.IsProxied = false;
    }, createIfNotExists: true)
    .WithDataVolume("pole-mongo-data")
    .WithLifetime(ContainerLifetime.Persistent)
    .WithReplicaSet();
var mongoReplicaSet = builder
    .AddMongoReplicaSet("pole-database", mongoServer.Resource);

var keycloakUsername = builder.AddParameter("keycloak-username");
var keycloakPassword = builder.AddParameter("keycloak-password", secret: true);
var keycloak = builder.AddKeycloak("pole-keycloak",8080, keycloakUsername, keycloakPassword)
    .WithRealmImport("../../../infrastructure/keycloak/realms/pole.control-plane.realm.json")
    .WithEnvironment("KC_HTTP_MANAGEMENT_SCHEME", "http")
    .WithEndpoint("management", endpoint => endpoint.UriScheme = "http")
    .WithDataVolume("pole-keycloak-data")
    .WithLifetime(ContainerLifetime.Persistent);

var rabbitMqUsername = builder.AddParameter("rabbitmq-username");
var rabbitMqPassword = builder.AddParameter("rabbitmq-password", secret: true);
var rabbitMq = builder.AddRabbitMQ("pole-rabbitmq", rabbitMqUsername, rabbitMqPassword)
    .WithManagementPlugin()
    .WithDataVolume("pole-rabbitmq-data")
    .WithLifetime(ContainerLifetime.Persistent);

var api = builder.AddProject<Projects.Pole_Server>("pole-server")
    .WithReference(keycloak)
    .WithReference(mongoServer)
    .WithReference(mongoReplicaSet)
    .WithReference(rabbitMq)
    .WaitFor(keycloak)
    .WaitFor(mongoServer)
    .WaitFor(mongoReplicaSet)
    .WaitFor(rabbitMq);

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

    builder.AddContainer("pole-buildkit", "moby/buildkit", "v0.30.0")
        .WithBindMount("BuildKit/buildkitd.toml", "/etc/buildkit/buildkitd.toml", isReadOnly: true)
        .WithArgs(
            "--addr", "tcp://0.0.0.0:1234",
            "--config", "/etc/buildkit/buildkitd.toml")
        .WithContainerRuntimeArgs("--privileged")
        .WithEndpoint("grpc", endpoint =>
        {
            endpoint.Port = 1234;
            endpoint.TargetPort = 1234;
            endpoint.IsProxied = false;
            endpoint.UriScheme = "tcp";
        })
        .WithVolume("pole-buildkit-data", "/var/lib/buildkit")
        .WithLifetime(ContainerLifetime.Persistent)
        .WithExplicitStart();

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
            builder.Configuration["LocalSourceBuilds:BuildKitAddress"] ?? "tcp://localhost:1234")
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
if (githubParameterNames.All(name =>
        !string.IsNullOrWhiteSpace(builder.Configuration[$"Parameters:{name}"])))
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
        .WithEnvironment("GitHubApp__FrontendBaseUrl", "http://localhost:5173");
}

var blog = builder.AddProject<Projects.Blog>("blog")
    .WithReference(api)
    .WaitFor(api)
    .WithExplicitStart();

var frontend = builder.AddViteApp("frontend", "../frontend")
    .WithEndpoint("http", (endpointAnnotation) =>
    {
        endpointAnnotation.Port = 5173;
    })
    .WithReference(api)
    .WaitFor(api)
    .WithExternalHttpEndpoints();

builder.Build().Run();
