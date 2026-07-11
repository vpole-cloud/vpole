using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Xunit;

namespace Pole.Tests.E2E.Infrastructure;

public sealed class PoleE2EFixture : IAsyncLifetime
{
    private const string KeycloakAdminUsername = "pole-e2e-admin";
    private readonly CancellationTokenSource _shutdown = new(TimeSpan.FromMinutes(10));
    private readonly List<Func<CancellationToken, Task>> _cleanup = [];
    private readonly string _githubClientSecret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private readonly string _keycloakAdminPassword = $"E2e-{Guid.NewGuid():N}-1!";
    private readonly string _kubernetesNamespacePrefix = $"e2e-{Guid.NewGuid():N}"[..12];
    private readonly string _mongoPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private readonly string _rabbitMqPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private readonly SourceBuildSettings? _sourceBuildSettings = SourceBuildSettings.TryLoad();
    private readonly string _stubControlKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private DistributedApplication? _application;
    private KeycloakAdminClient? _keycloak;
    private string? _scratchDirectory;

    public HttpClient Api { get; private set; } = null!;
    public HttpClient Stub { get; private set; } = null!;
    public Uri FrontendBaseAddress { get; private set; } = null!;
    public TestUser Alpha { get; private set; } = null!;
    public TestUser Beta { get; private set; } = null!;
    public bool SourceBuildsEnabled => _sourceBuildSettings is not null;

    public async Task InitializeAsync()
    {
        _scratchDirectory = Path.Combine(Path.GetTempPath(), "pole-e2e", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_scratchDirectory);
        var privateKeyPath = Path.Combine(_scratchDirectory, "github-app-private-key.pem");
        using (var rsa = RSA.Create(2048))
            await File.WriteAllTextAsync(privateKeyPath, rsa.ExportPkcs8PrivateKeyPem(), _shutdown.Token);

        var githubClientId = $"pole-e2e-client-{Guid.NewGuid():N}";
        var appHost = await DistributedApplicationTestingBuilder.CreateAsync<Projects.Pole_AppHost>(
            CreateAppHostArguments(
                privateKeyPath,
                githubClientId,
                _sourceBuildSettings,
                _scratchDirectory,
                _kubernetesNamespacePrefix,
                _keycloakAdminPassword,
                _githubClientSecret,
                _mongoPassword,
                _rabbitMqPassword),
            _shutdown.Token);

        var api = appHost.CreateResourceBuilder<ProjectResource>("pole-server");
        var frontend = appHost.CreateResourceBuilder<ProjectResource>("frontend");
        var githubStub = appHost
            .AddProject(
                "github-stub",
                "../../../tests/Pole.Tests.E2E.GitHubStub/Pole.Tests.E2E.GitHubStub.csproj",
                launchProfileName: null)
            .WithHttpEndpoint(name: "http", env: "ASPNETCORE_HTTP_PORTS")
            .WithHttpHealthCheck("/healthz", endpointName: "http");

        api.WithEnvironment("GitHubApp__Enabled", "true")
            .WithEnvironment("GitHubApp__ClientId", githubClientId)
            .WithEnvironment("GitHubApp__ClientSecret", _githubClientSecret)
            .WithEnvironment("GitHubApp__AppSlug", "pole-e2e")
            .WithEnvironment("GitHubApp__PrivateKeyPath", privateKeyPath)
            .WithEnvironment("GitHubApp__ApiBaseUrl", githubStub.GetEndpoint("http"))
            .WithEnvironment("GitHubApp__WebBaseUrl", githubStub.GetEndpoint("http"))
            .WithEnvironment("GitHubApp__PublicBaseUrl", api.GetEndpoint("http"))
            .WithEnvironment("GitHubApp__FrontendBaseUrl", frontend.GetEndpoint("http"));
        githubStub
            .WithEnvironment("GitHubStub__CallbackBaseUrl", api.GetEndpoint("http"))
            .WithEnvironment("GitHubStub__ControlKey", _stubControlKey);

        _application = await appHost.BuildAsync(_shutdown.Token);
        await _application.StartAsync(_shutdown.Token);
        await _application.ResourceNotifications.WaitForResourceHealthyAsync("pole-keycloak", _shutdown.Token);
        await _application.ResourceNotifications.WaitForResourceHealthyAsync("pole-server", _shutdown.Token);
        await _application.ResourceNotifications.WaitForResourceHealthyAsync("frontend", _shutdown.Token);
        await _application.ResourceNotifications.WaitForResourceHealthyAsync("github-stub", _shutdown.Token);

        Api = _application.CreateHttpClient("pole-server");
        Stub = _application.CreateHttpClient("github-stub");
        Stub.DefaultRequestHeaders.Add("X-Pole-E2E-Stub-Key", _stubControlKey);
        FrontendBaseAddress = _application.CreateHttpClient("frontend").BaseAddress
                              ?? throw new InvalidOperationException("Aspire did not resolve the frontend endpoint.");
        _keycloak = new KeycloakAdminClient(
            _application.CreateHttpClient("pole-keycloak"),
            KeycloakAdminUsername,
            _keycloakAdminPassword);

        await ConfigureInteractiveClientAsync(_shutdown.Token);
        var directGrantClient = await _keycloak.CreateDirectGrantClientAsync(
            $"pole.e2e.direct-{Guid.NewGuid():N}",
            _shutdown.Token);
        _cleanup.Add(ct => _keycloak.DeleteClientAsync(directGrantClient.InternalId, ct));

        var bootstrap = await CreateBootstrapUserAsync(_shutdown.Token);
        await _keycloak.AssignRealmRoleAsync(bootstrap.KeycloakUserId, "pole-platform-admin", _shutdown.Token);
        var bootstrapToken = await _keycloak.GetPasswordGrantTokenAsync(
            directGrantClient.ClientId,
            directGrantClient.Secret,
            bootstrap.Email,
            bootstrap.Password,
            _shutdown.Token);

        Alpha = await CreatePlatformUserAsync("alpha", bootstrapToken, directGrantClient, _shutdown.Token);
        Beta = await CreatePlatformUserAsync("beta", bootstrapToken, directGrantClient, _shutdown.Token);
    }

    public async Task DisposeAsync()
    {
        var cancellationToken = new CancellationTokenSource(TimeSpan.FromMinutes(2)).Token;
        foreach (var action in _cleanup.AsEnumerable().Reverse())
        {
            try
            {
                await action(cancellationToken);
            }
            catch
            {
                // Ephemeral AppHost resources are disposed below; keep attempting later cleanup actions.
            }
        }

        await StopSourceBuildInfrastructureAsync(cancellationToken);
        if (_application is not null)
            await _application.DisposeAsync();
        _shutdown.Dispose();

        if (!string.IsNullOrWhiteSpace(_scratchDirectory) && Directory.Exists(_scratchDirectory))
        {
            try
            {
                Directory.Delete(_scratchDirectory, recursive: true);
            }
            catch
            {
                // Test diagnostics and a locked process should not hide the original test result.
            }
        }
    }

    public HttpRequestMessage CreateAuthenticatedRequest(HttpMethod method, string path, TestUser user, string? tenantId = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", user.AccessToken);
        if (!string.IsNullOrWhiteSpace(tenantId))
            request.Headers.Add("X-Pole-Tenant-ID", tenantId);
        return request;
    }

    public async Task StartSourceBuildInfrastructureAsync(CancellationToken cancellationToken)
    {
        if (_sourceBuildSettings is null || _application is null)
            throw new InvalidOperationException("The source-build lane is not enabled for this run.");

        var harborStart = await _application.ResourceCommands.ExecuteCommandAsync(
            "pole-harbor",
            KnownResourceCommands.StartCommand,
            cancellationToken);
        if (!harborStart.Success)
            throw new InvalidOperationException($"Aspire could not start Harbor: {harborStart.Message}");

        var buildKitStart = await _application.ResourceCommands.ExecuteCommandAsync(
            "pole-buildkit",
            KnownResourceCommands.StartCommand,
            cancellationToken);
        if (!buildKitStart.Success)
            throw new InvalidOperationException($"Aspire could not start BuildKit: {buildKitStart.Message}");

        await _application.ResourceNotifications.WaitForResourceAsync(
            "pole-buildkit",
            KnownResourceStates.Running,
            cancellationToken);
        await WaitForHarborHealthAsync(cancellationToken);
    }

    public async Task StopBuildKitAsync(CancellationToken cancellationToken)
    {
        if (_application is null)
            throw new InvalidOperationException("The AppHost has not started.");

        var stop = await _application.ResourceCommands.ExecuteCommandAsync(
            "pole-buildkit",
            KnownResourceCommands.StopCommand,
            cancellationToken);
        if (!stop.Success)
            throw new InvalidOperationException($"Aspire could not stop BuildKit: {stop.Message}");
    }

    public async Task DeleteHarborProjectAsync(string tenantId, CancellationToken cancellationToken)
    {
        if (_sourceBuildSettings is null)
            return;

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(tenantId));
        var projectName = $"pole-{Convert.ToHexStringLower(hash.AsSpan(0, 16))}";
        using var client = new HttpClient { BaseAddress = new Uri(_sourceBuildSettings.HarborBaseUrl) };
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"admin:{_sourceBuildSettings.HarborAdminPassword}"));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        using var response = await client.DeleteAsync(
            $"/api/v2.0/projects/{Uri.EscapeDataString(projectName)}",
            cancellationToken);
        if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.NotFound)
            throw new HttpRequestException($"Harbor did not delete the E2E project: {(int)response.StatusCode}.");
    }

    public async Task<string> GetHarborArtifactDigestAsync(
        string tenantId,
        string webServiceId,
        string buildJobId,
        CancellationToken cancellationToken)
    {
        if (_sourceBuildSettings is null)
            throw new InvalidOperationException("The source-build lane is not enabled for this run.");

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(tenantId));
        var projectName = $"pole-{Convert.ToHexStringLower(hash.AsSpan(0, 16))}";
        var repository = Uri.EscapeDataString($"web-services/{webServiceId}");
        using var client = new HttpClient { BaseAddress = new Uri(_sourceBuildSettings.HarborBaseUrl) };
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"admin:{_sourceBuildSettings.HarborAdminPassword}"));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        using var response = await client.GetAsync(
            $"/api/v2.0/projects/{Uri.EscapeDataString(projectName)}/repositories/{repository}/artifacts/build-{buildJobId}",
            cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        return document.RootElement.GetProperty("digest").GetString()
               ?? throw new InvalidOperationException("Harbor returned an artifact without a digest.");
    }

    public async Task WaitForNamespaceDeletionAsync(string resourceGroupId, CancellationToken cancellationToken)
    {
        if (_sourceBuildSettings is null)
            return;

        var tenantSegment = Alpha.DefaultTenantId[..Math.Min(8, Alpha.DefaultTenantId.Length)];
        var groupSegment = resourceGroupId[..Math.Min(8, resourceGroupId.Length)];
        var namespaceName = $"{_kubernetesNamespacePrefix}-t-{tenantSegment}-rg-{groupSegment}";
        if (await RunKubectlAsync(["get", "namespace", namespaceName], cancellationToken) != 0)
            return;

        var exitCode = await RunKubectlAsync(
            ["wait", "--for=delete", $"namespace/{namespaceName}", "--timeout=2m"],
            cancellationToken);
        if (exitCode != 0)
            throw new TimeoutException($"Kubernetes namespace '{namespaceName}' was not deleted.");

    }

    private static string[] CreateAppHostArguments(
        string privateKeyPath,
        string githubClientId,
        SourceBuildSettings? sourceBuildSettings,
        string scratchDirectory,
        string kubernetesNamespacePrefix,
        string keycloakAdminPassword,
        string githubClientSecret,
        string mongoPassword,
        string rabbitMqPassword)
    {
        var arguments = new List<string>
        {
        "E2E:Enabled=true",
        $"E2E:KubernetesNamespacePrefix={kubernetesNamespacePrefix}",
        "Parameters:mongo-username=pole-e2e",
        $"Parameters:mongo-password={mongoPassword}",
        $"Parameters:keycloak-username={KeycloakAdminUsername}",
        $"Parameters:keycloak-password={keycloakAdminPassword}",
        "Parameters:rabbitmq-username=pole-e2e",
        $"Parameters:rabbitmq-password={rabbitMqPassword}",
        $"Parameters:github-app-client-id={githubClientId}",
        $"Parameters:github-app-client-secret={githubClientSecret}",
        "Parameters:github-app-slug=pole-e2e",
        $"Parameters:github-app-private-key-path={privateKeyPath}",
        "Parameters:github-app-public-base-url=http://placeholder.invalid"
        };
        if (sourceBuildSettings is not null)
        {
            arguments.AddRange(
            [
                "LocalSourceBuilds:Enabled=true",
                $"LocalSourceBuilds:HarborComposeDirectory={sourceBuildSettings.HarborComposeDirectory}",
                $"LocalSourceBuilds:HarborBaseUrl={sourceBuildSettings.HarborBaseUrl}",
                $"LocalSourceBuilds:HarborRegistryHost={sourceBuildSettings.HarborRegistryHost}",
                $"LocalSourceBuilds:BuildctlPath={sourceBuildSettings.BuildctlPath}",
                $"Parameters:harbor-admin-password={sourceBuildSettings.HarborAdminPassword}",
                $"E2E:WorkspaceRoot={Path.Combine(scratchDirectory, "source-workspace")}"
            ]);
        }

        return arguments.ToArray();
    }

    private async Task ConfigureInteractiveClientAsync(CancellationToken cancellationToken)
    {
        var client = await _keycloak!.GetClientAsync("pole.control-plane.ui", cancellationToken);
        var originalUiClient = client.DeepClone().AsObject();
        var uiClientInternalId = client["id"]?.GetValue<string>()
                                 ?? throw new InvalidOperationException("Keycloak did not return the UI client id.");
        var frontendOrigin = FrontendBaseAddress.GetLeftPart(UriPartial.Authority);
        client["redirectUris"] = new JsonArray($"{frontendOrigin}/*");
        client["webOrigins"] = new JsonArray(frontendOrigin);
        var attributes = client["attributes"] as JsonObject ?? new JsonObject();
        client["attributes"] = attributes;
        attributes["post.logout.redirect.uris"] = "+";
        await _keycloak.UpdateClientAsync(uiClientInternalId, client, cancellationToken);
        _cleanup.Add(ct => _keycloak.UpdateClientAsync(uiClientInternalId, originalUiClient, ct));
    }

    private async Task<TestUser> CreateBootstrapUserAsync(CancellationToken cancellationToken)
    {
        var password = $"Bootstrap-{Guid.NewGuid():N}-1!";
        var email = $"bootstrap-{Guid.NewGuid():N}@e2e.pole.test";
        var keycloakUserId = await _keycloak!.CreateUserAsync(email, "E2E", "Bootstrap", password, cancellationToken);
        _cleanup.Add(ct => _keycloak.DeleteUserAsync(keycloakUserId, ct));
        return new TestUser(email, password, keycloakUserId, string.Empty, string.Empty);
    }

    private async Task<TestUser> CreatePlatformUserAsync(
        string prefix,
        string bootstrapToken,
        DirectGrantClient directGrantClient,
        CancellationToken cancellationToken)
    {
        var email = $"{prefix}-{Guid.NewGuid():N}@e2e.pole.test";
        using var create = new HttpRequestMessage(HttpMethod.Post, "/api/v1/users")
        {
            Content = JsonContent.Create(new { email, firstName = "E2E", lastName = prefix })
        };
        create.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bootstrapToken);
        using var response = await Api.SendAsync(create, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        var keycloakUserId = document.RootElement.GetProperty("id").GetString()
                             ?? throw new InvalidOperationException("Pole did not return the created user id.");

        var password = $"{prefix}-{Guid.NewGuid():N}-1!";
        await _keycloak!.SetPasswordAsync(keycloakUserId, password, cancellationToken);
        await _keycloak.MakeUserInteractiveAsync(keycloakUserId, cancellationToken);
        _cleanup.Add(ct => _keycloak.DeleteUserAsync(keycloakUserId, ct));

        var accessToken = await _keycloak.GetPasswordGrantTokenAsync(
            directGrantClient.ClientId,
            directGrantClient.Secret,
            email,
            password,
            cancellationToken);
        var tenantId = await WaitForDefaultTenantAsync(accessToken, cancellationToken);
        return new TestUser(email, password, keycloakUserId, accessToken, tenantId);
    }

    private async Task<string> WaitForDefaultTenantAsync(string accessToken, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(2);
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/tenants");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var response = await Api.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
                var tenant = document.RootElement.EnumerateArray()
                    .FirstOrDefault(item => item.TryGetProperty("isDefault", out var isDefault) && isDefault.GetBoolean());
                if (tenant.ValueKind != JsonValueKind.Undefined)
                    return tenant.GetProperty("id").GetString()
                           ?? throw new InvalidOperationException("Default tenant has no id.");
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        throw new TimeoutException("The default tenant was not created from the UserCreated outbox event.");
    }

    private async Task WaitForHarborHealthAsync(CancellationToken cancellationToken)
    {
        using var client = new HttpClient { BaseAddress = new Uri(_sourceBuildSettings!.HarborBaseUrl) };
        var deadline = DateTimeOffset.UtcNow.AddMinutes(3);
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                using var response = await client.GetAsync("/api/v2.0/health", cancellationToken);
                if (response.IsSuccessStatusCode)
                    return;
            }
            catch (HttpRequestException)
            {
                // Harbor's Compose topology is still starting.
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }

        throw new TimeoutException("Harbor did not report healthy within three minutes.");
    }

    private async Task StopSourceBuildInfrastructureAsync(CancellationToken cancellationToken)
    {
        if (_sourceBuildSettings is null)
            return;

        if (_application is not null)
        {
            try
            {
                await _application.ResourceCommands.ExecuteCommandAsync(
                    "pole-buildkit",
                    KnownResourceCommands.StopCommand,
                    cancellationToken);
                await _application.ResourceCommands.ExecuteCommandAsync(
                    "pole-harbor",
                    KnownResourceCommands.StopCommand,
                    cancellationToken);
            }
            catch
            {
                // Always attempt the Compose cleanup below.
            }
        }

        var process = new Process
        {
            StartInfo = new ProcessStartInfo("docker")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add("compose");
        process.StartInfo.ArgumentList.Add("--project-directory");
        process.StartInfo.ArgumentList.Add(_sourceBuildSettings.HarborComposeDirectory);
        process.StartInfo.ArgumentList.Add("down");
        process.StartInfo.ArgumentList.Add("--remove-orphans");
        process.StartInfo.ArgumentList.Add("--volumes");

        try
        {
            process.Start();
            await process.WaitForExitAsync(cancellationToken);
        }
        catch
        {
            // The test's primary failure is more actionable; the runner teardown must be checked separately.
        }
        finally
        {
            process.Dispose();
        }
    }

    private static async Task<int> RunKubectlAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("kubectl")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        await process.WaitForExitAsync(cancellationToken);
        return process.ExitCode;
    }
}

public sealed record TestUser(
    string Email,
    string Password,
    string KeycloakUserId,
    string AccessToken,
    string DefaultTenantId);

internal sealed record SourceBuildSettings(
    string HarborComposeDirectory,
    string HarborBaseUrl,
    string HarborRegistryHost,
    string HarborAdminPassword,
    string BuildctlPath)
{
    public static SourceBuildSettings? TryLoad()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("POLE_E2E_SOURCE_BUILDS"), "1", StringComparison.Ordinal))
            return null;

        return new SourceBuildSettings(
            Required("POLE_E2E_HARBOR_COMPOSE_DIR"),
            Environment.GetEnvironmentVariable("POLE_E2E_HARBOR_BASE_URL") ?? "http://localhost:5000",
            Environment.GetEnvironmentVariable("POLE_E2E_HARBOR_REGISTRY_HOST") ?? "host.docker.internal:5000",
            Required("POLE_E2E_HARBOR_ADMIN_PASSWORD"),
            Required("POLE_E2E_BUILDCTL_PATH"));
    }

    private static string Required(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"{name} is required when POLE_E2E_SOURCE_BUILDS=1.");
}
