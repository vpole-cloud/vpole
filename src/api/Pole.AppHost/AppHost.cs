using Pole.AppHost.MongoDb;
using Pole.AppHost.Realtime;
using Microsoft.Extensions.Configuration;

var builder = DistributedApplication.CreateBuilder(args);
var e2eEnabled = builder.Configuration.GetValue("E2E:Enabled", false);
var realtimeEnabled = builder.Configuration.GetValue("Realtime:Enabled", false);
var e2eBuildKitPort = e2eEnabled ? builder.Configuration.GetValue<int?>("E2E:BuildKitPort") : null;
var e2eKeycloakPort = e2eEnabled ? builder.Configuration.GetValue<int?>("E2E:KeycloakPort") : null;
var mongoEntrypointPath = Path.Combine(builder.AppHostDirectory, "MongoDb", "mongo-entrypoint.sh");
var mongoEntrypointScript = File.ReadAllText(mongoEntrypointPath);

var mongoUsername = builder.AddParameter("mongo-username");
var mongoPassword = builder.AddParameter("mongo-password", secret: true);
var mongoServer = builder.AddMongoDB("pole-mongo", userName: mongoUsername, password: mongoPassword)
    .WithImageTag("7.0")
    // Avoid executing a host bind mount, which rootless SELinux runtimes can deny.
    .WithEntrypoint("/bin/bash")
    .WithArgs("-c", mongoEntrypointScript, "mongo-entrypoint")
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
var envelopeEncryptionActiveKeyId = builder.AddParameter("envelope-encryption-active-key-id");
var envelopeEncryptionActiveKeyVersion = builder.AddParameter("envelope-encryption-active-key-version");
var envelopeEncryptionActiveKeyPath = builder.AddParameter("envelope-encryption-active-key-path", secret: true);
var retainedEnvelopeKeyParameterNames = new[]
{
    "envelope-encryption-retained-key-id",
    "envelope-encryption-retained-key-version",
    "envelope-encryption-retained-key-path"
};
var retainedEnvelopeKeyParametersPresent = retainedEnvelopeKeyParameterNames.Count(
    name => !string.IsNullOrWhiteSpace(builder.Configuration[$"Parameters:{name}"]));
if (retainedEnvelopeKeyParametersPresent is > 0 and < 3)
{
    throw new InvalidOperationException(
        "The retained envelope-encryption key ID, version, and path parameters must be configured together.");
}
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
    .WithHttpEndpoint(targetPort: 5224, name: "http")
    .WithHttpEndpoint(targetPort: 8081, name: "functions-public")
    .WithHttpEndpoint(targetPort: 8082, name: "functions-internal")
    .WithEnvironment("Functions__Gateway__ControlPort", "5224")
    .WithEnvironment("EnvelopeEncryption__ActiveProviderId", "local-key-file")
    .WithEnvironment("EnvelopeEncryption__LocalKeyFile__ActiveKeyId", envelopeEncryptionActiveKeyId)
    .WithEnvironment("EnvelopeEncryption__LocalKeyFile__ActiveKeyVersion", envelopeEncryptionActiveKeyVersion)
    .WithEnvironment("EnvelopeEncryption__LocalKeyFile__Keys__0__Id", envelopeEncryptionActiveKeyId)
    .WithEnvironment("EnvelopeEncryption__LocalKeyFile__Keys__0__Version", envelopeEncryptionActiveKeyVersion)
    .WithEnvironment("EnvelopeEncryption__LocalKeyFile__Keys__0__Path", envelopeEncryptionActiveKeyPath)
    .WithReference(keycloak)
    .WithReference(mongoServer)
    .WithReference(mongoReplicaSet)
    .WithReference(rabbitMq);
if (retainedEnvelopeKeyParametersPresent == retainedEnvelopeKeyParameterNames.Length)
{
    var retainedKeyId = builder.AddParameter("envelope-encryption-retained-key-id");
    var retainedKeyVersion = builder.AddParameter("envelope-encryption-retained-key-version");
    var retainedKeyPath = builder.AddParameter("envelope-encryption-retained-key-path", secret: true);
    api.WithEnvironment("EnvelopeEncryption__LocalKeyFile__Keys__1__Id", retainedKeyId)
        .WithEnvironment("EnvelopeEncryption__LocalKeyFile__Keys__1__Version", retainedKeyVersion)
        .WithEnvironment("EnvelopeEncryption__LocalKeyFile__Keys__1__Path", retainedKeyPath);
}
if (e2eEnabled)
{
    api
        .WithEndpoint("http", endpoint => endpoint.Port = null)
        .WithEndpoint("functions-public", endpoint => endpoint.Port = null)
        .WithEndpoint("functions-internal", endpoint => endpoint.Port = null)
        .WaitFor(mongoReplicaSet)
        .WaitFor(rabbitMq);
}
else
{
    api
        // Local launch settings bind these same host ports directly; Aspire cannot proxy them.
        .WithEndpoint("http", endpoint => endpoint.IsProxied = false)
        .WithEndpoint("functions-public", endpoint => endpoint.IsProxied = false)
        .WithEndpoint("functions-internal", endpoint => endpoint.IsProxied = false)
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
            builder.Configuration["LocalSourceBuilds:ComposeCli"] ?? "docker",
            Path.GetFullPath(harborComposeDirectory),
            "compose",
            "up",
            "--remove-orphans")
        .WithExplicitStart();

    var buildKit = builder.AddContainer("pole-buildkit", "moby/buildkit", "v0.31.2")
        .WithBindMount("BuildKit/buildkitd.toml", "/etc/buildkit/buildkitd.toml", isReadOnly: true)
        .WithArgs(
            "--addr", "tcp://0.0.0.0:1234",
            "--config", "/etc/buildkit/buildkitd.toml")
        .WithContainerRuntimeArgs("--privileged")
        .WithEndpoint("grpc", endpoint =>
        {
            if (!e2eEnabled)
                endpoint.Port = 1234;
            else if (e2eBuildKitPort is { } buildKitPort)
                endpoint.Port = buildKitPort;
            endpoint.TargetPort = 1234;
            // buildctl uses a streaming gRPC session; exposing the container endpoint directly
            // avoids an Aspire proxy altering that connection in the isolated E2E graph.
            endpoint.IsProxied = false;
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

if (realtimeEnabled)
{
    var realtime = builder.AddRedis("realtime")
        .WithArgs(context =>
        {
            // Aspire wraps redis-server in a shell command for its publish model.
            if (context.Args.Count >= 2 && context.Args[0] is "-c")
                context.Args[1] = $"{context.Args[1]} --appendonly yes --appendfsync everysec --maxmemory-policy noeviction";
            else
                foreach (var argument in new[] { "--appendonly", "yes", "--appendfsync", "everysec", "--maxmemory-policy", "noeviction" })
                    context.Args.Add(argument);
        });
    if (!e2eEnabled)
        realtime.WithDataVolume("pole-realtime-data").WithLifetime(ContainerLifetime.Persistent);
    else
        realtime.WithPersistence().WithLifetime(ContainerLifetime.Session);
    var certificatePath = builder.Configuration["Realtime:KeyProtectionCertificatePath"];
    if (string.IsNullOrWhiteSpace(certificatePath))
    {
        if (!builder.ExecutionContext.IsRunMode)
            throw new InvalidOperationException("Publishing enabled realtime requires its key-protection certificate path.");
        certificatePath = RealtimeDevelopmentCertificate.GetOrCreate(builder.AppHostDirectory);
    }
    api.WithReference(realtime).WaitFor(realtime)
        .WithEnvironment("Realtime__Enabled", "true")
        .WithEnvironment("Realtime__KeyProtectionCertificatePath", certificatePath);
    if (!string.IsNullOrWhiteSpace(builder.Configuration["Parameters:realtime-key-protection-certificate-password"]))
        api.WithEnvironment("Realtime__KeyProtectionCertificatePassword",
            builder.AddParameter("realtime-key-protection-certificate-password", secret: true));
}

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
api.WithEnvironment("Realtime__AllowedOrigins__0", frontend.GetEndpoint("http"));
api.WithEnvironment("KeycloakAdmin__BaseUrl", keycloak.GetEndpoint("http"));
if (e2eKeycloakPort is { } keycloakPort)
{
    var keycloakIssuer = "http://localhost:" + keycloakPort + "/realms/pole.control-plane";
    api.WithEnvironment("Authentication__ValidIssuer", keycloakIssuer);
    api.WithEnvironment("Cli__Issuer", keycloakIssuer);
}
if (githubAppEnabled)
{
    api.WithEnvironment("GitHubApp__FrontendBaseUrl", frontend.GetEndpoint("http"));
    if (e2eEnabled)
        api.WithEnvironment("GitHubApp__PublicBaseUrl", api.GetEndpoint("http"));
}

builder.Build().Run();
