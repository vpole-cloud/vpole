using System.Diagnostics;
using System.ComponentModel;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.JavaScript;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Pole.Tests.E2E.Infrastructure;

public sealed class PoleE2EFixture : IAsyncLifetime
{
    private const string KeycloakAdminUsername = "pole-e2e-admin";
    private readonly CancellationTokenSource _shutdown = new(TimeSpan.FromMinutes(10));
    private readonly List<Func<CancellationToken, Task>> _cleanup = [];
    private readonly TcpListener _buildKitPortReservation;
    private readonly int _buildKitPort;
    private readonly string _githubClientSecret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private readonly string _keycloakAdminPassword = $"E2e-{Guid.NewGuid():N}-1!";
    private readonly TcpListener _keycloakPortReservation = ReserveTcpPort();
    private readonly string _kubernetesNamespacePrefix = $"e2e-{Guid.NewGuid():N}"[..12];
    private readonly string _mongoPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private readonly string _rabbitMqPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private readonly SourceBuildSettings? _sourceBuildSettings;
    private readonly string _stubControlKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private DistributedApplication? _application;
    private KeycloakAdminClient? _keycloak;
    private string? _scratchDirectory;

    public PoleE2EFixture()
    {
        _sourceBuildSettings = E2ETestSettings.Load().SourceBuilds;
        _buildKitPortReservation = ReserveTcpPort();
        _buildKitPort = ((IPEndPoint)_buildKitPortReservation.LocalEndpoint).Port;
    }

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
        var envelopeEncryptionKeyPath = Path.Combine(_scratchDirectory, "envelope-encryption-v1.key");
        var envelopeEncryptionKey = RandomNumberGenerator.GetBytes(32);
        try
        {
            await File.WriteAllTextAsync(
                envelopeEncryptionKeyPath,
                Convert.ToBase64String(envelopeEncryptionKey),
                _shutdown.Token);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(envelopeEncryptionKey);
        }

        var githubClientId = $"pole-e2e-client-{Guid.NewGuid():N}";
        var appHost = await DistributedApplicationTestingBuilder.CreateAsync<Projects.Pole_AppHost>(
            CreateAppHostArguments(
                privateKeyPath,
                envelopeEncryptionKeyPath,
                githubClientId,
                _sourceBuildSettings,
                _scratchDirectory,
                _kubernetesNamespacePrefix,
                BuildKitPort,
                KeycloakPort,
                _keycloakAdminPassword,
                _githubClientSecret,
                _mongoPassword,
                _rabbitMqPassword),
            _shutdown.Token);

        var api = appHost.CreateResourceBuilder<ProjectResource>("pole-server");
        var frontend = appHost.CreateResourceBuilder<ViteAppResource>("frontend");
        if (_sourceBuildSettings is not null)
        {
            // The test server is a host process, whereas BuildKit is a proxyless container.
            // Give buildctl the reserved host endpoint rather than container service discovery.
            api.WithEnvironment(context =>
                context.EnvironmentVariables["BuildKit__Address"] = $"tcp://127.0.0.1:{BuildKitPort}");
        }
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
        _buildKitPortReservation.Stop();
        _keycloakPortReservation.Stop();
        await _application.StartAsync(_shutdown.Token);
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
        await WaitForKeycloakDiscoveryAsync(_application.CreateHttpClient("pole-keycloak"), _shutdown.Token);

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
        _buildKitPortReservation.Stop();
        _keycloakPortReservation.Stop();
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
        await WaitForBuildKitReadyAsync(cancellationToken);
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
        using var client = new HttpClient(new HttpClientHandler { UseCookies = false })
        {
            BaseAddress = new Uri(_sourceBuildSettings.HarborBaseUrl)
        };
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"admin:{_sourceBuildSettings.HarborAdminPassword}"));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        var repositoryNames = new List<string>();
        for (var page = 1; ; page++)
        {
            using var repositoriesResponse = await client.GetAsync(
                $"/api/v2.0/projects/{Uri.EscapeDataString(projectName)}/repositories?page={page}&page_size=100",
                cancellationToken);
            if (repositoriesResponse.StatusCode == System.Net.HttpStatusCode.NotFound)
                return;
            repositoriesResponse.EnsureSuccessStatusCode();
            using var repositories = JsonDocument.Parse(await repositoriesResponse.Content.ReadAsStreamAsync(cancellationToken));
            var currentPage = repositories.RootElement.EnumerateArray().ToArray();
            repositoryNames.AddRange(currentPage
                .Select(item => item.GetProperty("name").GetString())
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!));
            if (currentPage.Length < 100)
                break;
        }

        foreach (var repositoryName in repositoryNames)
        {
            var relativeRepositoryName = repositoryName.StartsWith($"{projectName}/", StringComparison.OrdinalIgnoreCase)
                ? repositoryName[(projectName.Length + 1)..]
                : repositoryName;
            // Harbor treats nested repository names as one route parameter. HttpClient/Uri
            // unescape the first level, so the embedded slash must be encoded twice.
            var encodedRepository = Uri.EscapeDataString(Uri.EscapeDataString(relativeRepositoryName));
            using var deleteRepositoryResponse = await client.DeleteAsync(
                $"/api/v2.0/projects/{Uri.EscapeDataString(projectName)}/repositories/{encodedRepository}",
                cancellationToken);
            if (!deleteRepositoryResponse.IsSuccessStatusCode &&
                deleteRepositoryResponse.StatusCode != System.Net.HttpStatusCode.NotFound)
            {
                throw new HttpRequestException(
                    $"Harbor did not delete the E2E repository: {(int)deleteRepositoryResponse.StatusCode}.");
            }
        }

        using var deleteProjectResponse = await client.DeleteAsync(
            $"/api/v2.0/projects/{Uri.EscapeDataString(projectName)}",
            cancellationToken);
        if (!deleteProjectResponse.IsSuccessStatusCode && deleteProjectResponse.StatusCode != System.Net.HttpStatusCode.NotFound)
            throw new HttpRequestException($"Harbor did not delete the E2E project: {(int)deleteProjectResponse.StatusCode}.");
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
        // See DeleteHarborProjectAsync: nested repository paths require double encoding.
        var repository = Uri.EscapeDataString(Uri.EscapeDataString($"web-services/{webServiceId}"));
        using var client = new HttpClient(new HttpClientHandler { UseCookies = false })
        {
            BaseAddress = new Uri(_sourceBuildSettings.HarborBaseUrl)
        };
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

        var namespaceName = GetNamespaceName(resourceGroupId);
        if (await RunKubectlAsync(["get", "namespace", namespaceName], cancellationToken) != 0)
            return;

        var exitCode = await RunKubectlAsync(
            ["wait", "--for=delete", $"namespace/{namespaceName}", "--timeout=2m"],
            cancellationToken);
        if (exitCode != 0)
            throw new TimeoutException($"Kubernetes namespace '{namespaceName}' was not deleted.");

    }

    public async Task WaitForNamespaceCreationAsync(string resourceGroupId, CancellationToken cancellationToken)
    {
        var namespaceName = GetNamespaceName(resourceGroupId);
        var deadline = DateTimeOffset.UtcNow.AddMinutes(2);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await RunKubectlAsync(["get", "namespace", namespaceName], cancellationToken) == 0)
                return;

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }

        throw new TimeoutException($"Kubernetes namespace '{namespaceName}' was not created.");
    }

    public Task<JsonElement> GetWebServiceDeploymentAsync(
        string resourceGroupId,
        string webServiceName,
        CancellationToken cancellationToken) =>
        ReadKubectlJsonAsync(
            ["get", "deployment", webServiceName, "--namespace", GetNamespaceName(resourceGroupId), "--output=json"],
            cancellationToken);

    public async Task<bool> KubernetesSecretExistsAsync(
        string resourceGroupId,
        string secretName,
        CancellationToken cancellationToken) =>
        await RunKubectlAsync(
            ["get", "secret", secretName, "--namespace", GetNamespaceName(resourceGroupId)],
            cancellationToken) == 0;

    public async Task<string> GetWebServiceDeploymentDiagnosticsAsync(CancellationToken cancellationToken)
    {
        if (_application is null)
            return string.Empty;

        var resourceLogger = _application.Services.GetRequiredService<ResourceLoggerService>();
        var diagnostics = new StringBuilder();
        using var diagnosticTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        diagnosticTimeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await foreach (var batch in resourceLogger.GetAllAsync("pole-server").WithCancellation(diagnosticTimeout.Token))
            {
                foreach (var line in batch)
                {
                    if (!line.Content.Contains("[web-service-deployment-failed]", StringComparison.Ordinal))
                        continue;

                    diagnostics.AppendLine(SanitizeDiagnosticLine(line.Content));
                    if (diagnostics.Length >= 4_000)
                        return diagnostics.ToString();
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Resource logs are a live stream. Diagnostics must not hide the original test failure.
        }

        return diagnostics.ToString();
    }

    public async Task<string> GetServerErrorDiagnosticsAsync(CancellationToken cancellationToken)
    {
        if (_application is null)
            return string.Empty;

        var resourceLogger = _application.Services.GetRequiredService<ResourceLoggerService>();
        var diagnostics = new StringBuilder();
        using var diagnosticTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        diagnosticTimeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await foreach (var batch in resourceLogger.GetAllAsync("pole-server").WithCancellation(diagnosticTimeout.Token))
            {
                foreach (var line in batch)
                {
                    if (!line.IsErrorMessage &&
                        !line.Content.Contains("exception", StringComparison.OrdinalIgnoreCase) &&
                        !line.Content.Contains("fail", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    diagnostics.AppendLine(SanitizeDiagnosticLine(line.Content));
                    if (diagnostics.Length >= 4_000)
                        return diagnostics.ToString();
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Resource logs are a live stream. Diagnostics must not hide the original test failure.
        }

        return diagnostics.ToString();
    }

    public async Task<string> GetGitHubStubRequestDiagnosticsAsync(CancellationToken cancellationToken)
    {
        using var response = await Stub.GetAsync("/__e2e/requests", cancellationToken);
        if (!response.IsSuccessStatusCode)
            return string.Empty;

        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        return string.Join(
            Environment.NewLine,
            document.RootElement
                .EnumerateArray()
                .TakeLast(20)
                .Select(request =>
                    $"{request.GetProperty("method").GetString()} {PathWithoutQuery(request.GetProperty("path").GetString())}"));
    }

    private string GetNamespaceName(string resourceGroupId)
    {
        var tenantSegment = Alpha.DefaultTenantId[..Math.Min(8, Alpha.DefaultTenantId.Length)];
        var groupSegment = resourceGroupId[..Math.Min(8, resourceGroupId.Length)];
        return $"{_kubernetesNamespacePrefix}-t-{tenantSegment}-rg-{groupSegment}";
    }

    private static string[] CreateAppHostArguments(
        string privateKeyPath,
        string envelopeEncryptionKeyPath,
        string githubClientId,
        SourceBuildSettings? sourceBuildSettings,
        string scratchDirectory,
        string kubernetesNamespacePrefix,
        int buildKitPort,
        int keycloakPort,
        string keycloakAdminPassword,
        string githubClientSecret,
        string mongoPassword,
        string rabbitMqPassword)
    {
        var arguments = new List<string>
        {
        "E2E:Enabled=true",
        $"E2E:BuildKitPort={buildKitPort}",
        $"E2E:KeycloakPort={keycloakPort}",
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
        "Parameters:github-app-public-base-url=http://placeholder.invalid",
        "Parameters:envelope-encryption-active-key-id=pole-e2e",
        "Parameters:envelope-encryption-active-key-version=1",
        $"Parameters:envelope-encryption-active-key-path={envelopeEncryptionKeyPath}"
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

    private static string SanitizeDiagnosticLine(string line) =>
        Regex.Replace(
            Regex.Replace(
                Regex.Replace(line, "(?i)(authorization)\\s*[:=]\\s*(?:Basic|Bearer)\\s+\\S+", "$1=[redacted]"),
                "(?i)Bearer\\s+\\S+",
                "Bearer [redacted]"),
            "(?i)(password|token|secret)(?:[\\\"']\\s*)?[:=]\\s*(?:[\\\"'])?[^\\s,}\\]\\\"']+",
            "$1=[redacted]");

    private static string PathWithoutQuery(string? path) =>
        path?.Split('?', 2)[0] ?? string.Empty;

    private int KeycloakPort => ((IPEndPoint)_keycloakPortReservation.LocalEndpoint).Port;

    private int BuildKitPort => _buildKitPort;

    private static TcpListener ReserveTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return listener;
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
        if (!response.IsSuccessStatusCode)
        {
            var challenge = string.Join(", ", response.Headers.WwwAuthenticate.Select(value => value.ToString()));
            throw new HttpRequestException(
                $"Pole rejected the bootstrap user creation with {(int)response.StatusCode} ({response.ReasonPhrase}). " +
                $"WWW-Authenticate: {challenge}. Response body omitted to avoid reflecting credentials.",
                null,
                response.StatusCode);
        }
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
        HttpStatusCode? lastStatusCode = null;
        string? lastResponseBody = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/tenants");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var response = await Api.SendAsync(request, cancellationToken);
            lastStatusCode = response.StatusCode;
            if (response.IsSuccessStatusCode)
            {
                lastResponseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                using var document = JsonDocument.Parse(lastResponseBody);
                var tenant = document.RootElement.EnumerateArray()
                    .FirstOrDefault(item => item.TryGetProperty("isDefault", out var isDefault) && isDefault.GetBoolean());
                if (tenant.ValueKind != JsonValueKind.Undefined)
                    return tenant.GetProperty("id").GetString()
                           ?? throw new InvalidOperationException("Default tenant has no id.");
            }
            else
            {
                lastResponseBody = "(response body omitted to avoid reflecting credentials)";
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        throw new TimeoutException(
            "The default tenant was not created from the UserCreated outbox event. " +
            $"Last GET /api/v1/tenants response: {(int?)lastStatusCode} {lastStatusCode}; {lastResponseBody}");
    }

    private async Task WaitForHarborHealthAsync(CancellationToken cancellationToken)
    {
        using var client = new HttpClient(new HttpClientHandler { UseCookies = false })
        {
            BaseAddress = new Uri(_sourceBuildSettings!.HarborBaseUrl)
        };
        var deadline = DateTimeOffset.UtcNow.AddMinutes(3);
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                using var response = await client.GetAsync("/api/v2.0/health", cancellationToken);
                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException("Harbor API is not ready.");

                // The API can become healthy before the registry endpoint is ready to accept
                // BuildKit pushes. A 401 is the expected unauthenticated response from /v2/.
                using var registryResponse = await client.GetAsync("/v2/", cancellationToken);
                if (registryResponse.IsSuccessStatusCode ||
                    registryResponse.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // Harbor's Compose topology is still starting.
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }

        throw new TimeoutException("Harbor did not report healthy within three minutes.");
    }

    private async Task WaitForBuildKitReadyAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(1);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await IsBuildKitReadyAsync(cancellationToken))
                return;

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        throw new TimeoutException("BuildKit did not accept a buildctl connection on its E2E endpoint.");
    }

    private async Task<bool> IsBuildKitReadyAsync(CancellationToken cancellationToken)
    {
        if (_sourceBuildSettings is null)
            return false;

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _sourceBuildSettings.BuildctlPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add("--addr");
        process.StartInfo.ArgumentList.Add($"tcp://127.0.0.1:{BuildKitPort}");
        process.StartInfo.ArgumentList.Add("debug");
        process.StartInfo.ArgumentList.Add("workers");

        var started = false;
        try
        {
            started = process.Start();
            if (!started)
                return false;

            var standardOutput = process.StandardOutput.ReadToEndAsync();
            var standardError = process.StandardError.ReadToEndAsync();
            using var attemptTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attemptTimeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                await process.WaitForExitAsync(attemptTimeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None);
                }
            }

            if (!process.HasExited)
                return false;

            // Consume and discard the diagnostic output: it can contain daemon details, while
            // only the exit status is needed to prove the E2E endpoint is usable.
            await Task.WhenAll(standardOutput, standardError);
            return process.ExitCode == 0;
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or InvalidOperationException)
        {
            return false;
        }
        finally
        {
            if (started)
            {
                try
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // The short-lived readiness process has already exited.
                }
            }
        }
    }

    private static async Task WaitForKeycloakDiscoveryAsync(HttpClient keycloak, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(2);
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                using var response = await keycloak.GetAsync(
                    "/realms/pole.control-plane/.well-known/openid-configuration",
                    cancellationToken);
                if (response.IsSuccessStatusCode)
                    return;
            }
            catch (HttpRequestException)
            {
                // Keycloak is still importing the realm.
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        throw new TimeoutException("Keycloak did not expose OpenID discovery within two minutes.");
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
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(cancellationToken);
        await Task.WhenAll(standardOutput, standardError);
        return process.ExitCode;
    }

    private static async Task<JsonElement> ReadKubectlJsonAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("kubectl")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(cancellationToken);
        var output = await standardOutput;
        await standardError;
        if (process.ExitCode != 0)
            throw new InvalidOperationException("kubectl could not read the E2E Kubernetes resource.");

        using var document = JsonDocument.Parse(output);
        return document.RootElement.Clone();
    }
}

public sealed record TestUser(
    string Email,
    string Password,
    string KeycloakUserId,
    string AccessToken,
    string DefaultTenantId);

internal sealed record E2ETestSettings(SourceBuildSettings? SourceBuilds)
{
    public static E2ETestSettings Load()
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("testsettings.json", optional: true)
            .AddJsonFile("testsettings.local.json", optional: true)
            .Build();
        return FromConfiguration(configuration);
    }

    internal static E2ETestSettings FromConfiguration(IConfiguration configuration)
    {
        var sourceBuilds = configuration.GetSection("SourceBuilds");
        if (!bool.TryParse(sourceBuilds["Enabled"], out var enabled) || !enabled)
            return new E2ETestSettings((SourceBuildSettings?)null);

        return new E2ETestSettings(new SourceBuildSettings(
            Required(sourceBuilds, "HarborComposeDirectory"),
            Optional(sourceBuilds, "HarborBaseUrl", "http://localhost:5000"),
            Optional(sourceBuilds, "HarborRegistryHost", "host.docker.internal:5000"),
            Required(sourceBuilds, "HarborAdminPassword"),
            Optional(sourceBuilds, "BuildctlPath", "buildctl")));
    }

    private static string Optional(IConfigurationSection section, string name, string defaultValue) =>
        section[name] is { } value && !string.IsNullOrWhiteSpace(value)
            ? value
            : defaultValue;

    private static string Required(IConfigurationSection section, string name) =>
        section[name] is { } value && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException($"SourceBuilds:{name} is required when SourceBuilds:Enabled=true.");
}

internal sealed record SourceBuildSettings(
    string HarborComposeDirectory,
    string HarborBaseUrl,
    string HarborRegistryHost,
    string HarborAdminPassword,
    string BuildctlPath);
