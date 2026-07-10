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
