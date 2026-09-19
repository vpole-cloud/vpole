using System.ComponentModel;
using System.Diagnostics;
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
using k8s;
using k8s.Autorest;
using k8s.Models;
using Microsoft.Extensions.DependencyInjection;
using Pole.Testing;
using Xunit;
using MongoDB.Driver;
using Skrynia;

namespace Pole.Tests.E2E.Infrastructure;

public sealed class PoleE2EFixture : IAsyncLifetime
{
    private const string KeycloakAdminUsername = "pole-e2e-admin";
    private readonly CancellationTokenSource _shutdown = new(TimeSpan.FromMinutes(10));
    private readonly List<Func<CancellationToken, Task>> _cleanup = [];
    private readonly TestRunIdentity _identity = TestRunIdentity.Create();
    private readonly Dictionary<string, RecordedNamespace> _kubernetesNamespaces = new(StringComparer.Ordinal);
    private readonly string _githubClientSecret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private readonly string _keycloakAdminPassword = $"E2e-{Guid.NewGuid():N}-1!";
    private readonly TcpListener _keycloakPortReservation = ReserveTcpPort();
    private readonly string _kubernetesNamespacePrefix;
    private readonly string _mongoPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private readonly string _rabbitMqPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private readonly SelectedKubernetesLab _lab;
    private readonly KubernetesLabTarget _kubernetesTarget;
    private readonly IKubernetes _kubernetes;
    private readonly KubernetesLabServices _labServices;
    private readonly KubernetesLabTarget _serviceTarget;
    private readonly string _stubControlKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private DistributedApplication? _application;
    private KubernetesPortForwardSession? _buildKitPortForward;
    private KubernetesPortForwardSession? _harborPortForward;
    private HarborCredentials? _harborCredentials;
    private KeycloakAdminClient? _keycloak;
    private string? _scratchDirectory;

    public PoleE2EFixture()
    {
        _lab = KubernetesLabSelection.RequireSelection();
        _kubernetesTarget = _lab.Target;
        _labServices = _lab.State.Services
                       ?? throw new InvalidOperationException(
                           "The Aspire E2E suite requires a full Kubernetes lab with Harbor and BuildKit services.");
        if (_labServices.Harbor.TargetKey != _labServices.BuildKit.TargetKey)
            throw new InvalidOperationException("Harbor and BuildKit must belong to the same Kubernetes lab target.");
        _serviceTarget = _lab.State.GetRequiredTarget(_labServices.Harbor.TargetKey);
        _kubernetes = new Kubernetes(_kubernetesTarget.CreateConfiguration());
        // RuntimeCellNamespaceNameGenerator appends stable tenant/scope identities. Keep the
        // worktree at the front so recovery can also identify PVC-backed PVs after a namespace
        // has already disappeared.
        _kubernetesNamespacePrefix = $"pole-{_identity.WorktreeId}";
    }

    public HttpClient Api { get; private set; } = null!;
    public HttpClient Stub { get; private set; } = null!;
    public Uri FrontendBaseAddress { get; private set; } = null!;
    public TestUser Alpha { get; private set; } = null!;
    public TestUser Beta { get; private set; } = null!;
    public string PlatformAdminAccessToken { get; private set; } = null!;
    public KubernetesLabState LabState => _lab.State;
    public TestRunIdentity Identity => _identity;

    public async Task InitializeAsync()
    {
        await StartLabServiceAccessAsync(_shutdown.Token);
        _scratchDirectory = Path.Combine(AppContext.BaseDirectory, "pole-e2e", Guid.NewGuid().ToString("N"));
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
                _scratchDirectory,
                _kubernetesNamespacePrefix,
                KeycloakPort,
                _keycloakAdminPassword,
                _harborCredentials!.Password,
                _githubClientSecret,
                _mongoPassword,
                _rabbitMqPassword),
            _shutdown.Token);

        var api = appHost.CreateResourceBuilder<ProjectResource>("pole-server");
        var frontend = appHost.CreateResourceBuilder<ViteAppResource>("frontend");
        var harborAdminPassword = appHost.AddParameter("e2e-harbor-admin-password", secret: true);
        ConfigureLabServices(api, harborAdminPassword);
        ConfigureLabDeploymentTarget(api);
        api.WithEnvironment("Outbox__ProcessingTriggerInterval", "00:00:01");
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
        ConfigureOwnedContainerResources(appHost);

        _application = await appHost.BuildAsync(_shutdown.Token);
        _keycloakPortReservation.Stop();
        try
        {
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            startup.CancelAfter(TimeSpan.FromMinutes(3));
            await _application.StartAsync(startup.Token);
            await WaitForRequiredResourceHealthyAsync("pole-server", startup.Token);
        }
        catch
        {
            var diagnostics = await GetServerErrorDiagnosticsAsync(CancellationToken.None);
            await _application.DisposeAsync();
            _application = null;
            throw new InvalidOperationException("Pole startup failed. " + diagnostics);
        }
        await WaitForRequiredResourceHealthyAsync("frontend", _shutdown.Token);
        await WaitForRequiredResourceHealthyAsync("github-stub", _shutdown.Token);

        Api = _application.CreateHttpClient("pole-server", "http");
        Stub = _application.CreateHttpClient("github-stub", "http");
        Stub.DefaultRequestHeaders.Add("X-Pole-E2E-Stub-Key", _stubControlKey);
        FrontendBaseAddress = _application.CreateHttpClient("frontend", "http").BaseAddress
                              ?? throw new InvalidOperationException("Aspire did not resolve the frontend endpoint.");
        _keycloak = new KeycloakAdminClient(
            _application.CreateHttpClient("pole-keycloak", "http"),
            KeycloakAdminUsername,
            _keycloakAdminPassword);
        await WaitForKeycloakDiscoveryAsync(
            _application.CreateHttpClient("pole-keycloak", "http"),
            _shutdown.Token);

        await _keycloak.SetRealmAccessTokenLifespanAsync(TimeSpan.FromHours(1), _shutdown.Token);
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
        PlatformAdminAccessToken = bootstrapToken;

        Alpha = await CreatePlatformUserAsync("alpha", bootstrapToken, directGrantClient, _shutdown.Token);
        Beta = await CreatePlatformUserAsync("beta", bootstrapToken, directGrantClient, _shutdown.Token);
    }

    public async Task DisposeAsync()
    {
        _keycloakPortReservation.Stop();
        using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var cancellationToken = cleanupTimeout.Token;
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

        try
        {
            if (_application is not null)
            {
                await _shutdown.CancelAsync();
                using var stopTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
                await _application.StopAsync(stopTimeout.Token);
                await _application.DisposeAsync();
            }
        }
        finally
        {
            await DisposePortForwardAsync(_buildKitPortForward);
            _buildKitPortForward = null;
            await DisposePortForwardAsync(_harborPortForward);
            _harborPortForward = null;
            _kubernetes.Dispose();
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
    }

    private void ConfigureOwnedContainerResources(IDistributedApplicationBuilder appHost)
    {
        var labelArguments = _identity.NamespaceLabels
            .SelectMany(label => new[] { "--label", $"{label.Key}={label.Value}" })
            .ToArray();
        foreach (var resource in appHost.Resources.OfType<ContainerResource>())
        {
            appHost.CreateResourceBuilder(resource)
                .WithContainerRuntimeArgs(labelArguments);
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

    private async Task<string> GetServerResourceIdAsync(CancellationToken cancellationToken)
    {
        await foreach (var update in _application!.ResourceNotifications.WatchAsync(cancellationToken))
            if (update.Resource.Name == "pole-server" && update.ResourceId != update.Resource.Name)
                return update.ResourceId;
        throw new InvalidOperationException("Pole server instance was not found.");
    }

    public async Task WaitForOutboxPublicationAsync(string eventType, CancellationToken cancellationToken)
    {
        var logger = _application!.Services.GetRequiredService<ResourceLoggerService>();
        var resourceId = await GetServerResourceIdAsync(cancellationToken);
        while (true)
        {
            await foreach (var batch in logger.GetAllAsync(resourceId).WithCancellation(cancellationToken))
                if (batch.Any(line => line.Content.Contains("Published outbox message", StringComparison.Ordinal) &&
                    line.Content.Contains(eventType, StringComparison.Ordinal))) return;
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
    }

    public async Task RestartServerAsync(CancellationToken cancellationToken,
        Func<IMongoDatabase, Task>? inspect = null)
    {
        var application = _application ?? throw new InvalidOperationException("AppHost is not running.");
        var stop = await application.ResourceCommands.ExecuteCommandAsync(
            "pole-server", KnownResourceCommands.StopCommand, cancellationToken);
        if (!stop.Success)
            throw new InvalidOperationException("Could not stop Pole for the persistence check.");
        await application.ResourceNotifications.WaitForResourceAsync(
            "pole-server", KnownResourceStates.Finished, cancellationToken);
        try
        {
            if (inspect is not null)
            {
                if (string.Equals(Environment.GetEnvironmentVariable("POLE_E2E_DATABASE_PROVIDER"), "Skrynia", StringComparison.OrdinalIgnoreCase))
                {
                    using var owner = SkryniaClient.Open(Path.Combine(_scratchDirectory!, "pole.sqlite"));
                    await inspect(owner.Client.GetDatabase("pole-database"));
                }
                else
                {
                    using var client = new MongoClient(await application.GetConnectionStringAsync("pole-database", cancellationToken));
                    await inspect(client.GetDatabase("pole-database"));
                }
            }
        }
        finally
        {
            var restart = await application.ResourceCommands.ExecuteCommandAsync(
                "pole-server", KnownResourceCommands.StartCommand, cancellationToken);
            if (!restart.Success)
                throw new InvalidOperationException("Could not restart Pole for the persistence check.");
            await application.ResourceNotifications.WaitForResourceHealthyAsync("pole-server", cancellationToken);
        }
    }

    public HttpRequestMessage CreatePlatformAdminRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", PlatformAdminAccessToken);
        return request;
    }

    public async Task EnsureSourceBuildInfrastructureReadyAsync(CancellationToken cancellationToken)
    {
        await WaitForBuildKitReadyAsync(cancellationToken);
        await WaitForHarborHealthAsync(cancellationToken);
    }

    public async Task StopBuildKitAccessAsync()
    {
        var portForward = Interlocked.Exchange(ref _buildKitPortForward, null)
                          ?? throw new InvalidOperationException("BuildKit access has already been stopped.");
        await portForward.DisposeAsync();
    }

    public async Task DeleteHarborProjectAsync(string tenantId, CancellationToken cancellationToken)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(tenantId));
        var projectName = $"pole-{Convert.ToHexStringLower(hash.AsSpan(0, 16))}";
        using var client = new HttpClient(new HttpClientHandler { UseCookies = false })
        {
            BaseAddress = HarborBaseAddress
        };
        var credentials = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{_harborCredentials!.Username}:{_harborCredentials.Password}"));
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
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(tenantId));
        var projectName = $"pole-{Convert.ToHexStringLower(hash.AsSpan(0, 16))}";
        // See DeleteHarborProjectAsync: nested repository paths require double encoding.
        var repository = Uri.EscapeDataString(Uri.EscapeDataString($"web-services/{webServiceId}"));
        using var client = new HttpClient(new HttpClientHandler { UseCookies = false })
        {
            BaseAddress = HarborBaseAddress
        };
        var credentials = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{_harborCredentials!.Username}:{_harborCredentials.Password}"));
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
        if (!_kubernetesNamespaces.TryGetValue(resourceGroupId, out var recorded))
            return;

        var existing = await ReadNamespaceIfPresentAsync(recorded.Name, cancellationToken);
        if (existing is null)
            return;
        VerifyOwnedNamespace(existing, recorded.Uid);

        var exitCode = await RunKubectlAsync(
            ["wait", "--for=delete", $"namespace/{recorded.Name}", "--timeout=2m"],
            cancellationToken);
        if (exitCode != 0)
            throw new TimeoutException($"Kubernetes namespace '{recorded.Name}' was not deleted.");

    }

    public async Task WaitForNamespaceCreationAsync(string resourceGroupId, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(2);
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var request = CreateAuthenticatedRequest(
                HttpMethod.Get,
                $"/api/v1/resource-groups/{resourceGroupId}/runtime-cells",
                Alpha,
                Alpha.DefaultTenantId);
            using var response = await Api.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(
                await response.Content.ReadAsStreamAsync(cancellationToken));
            var cells = document.RootElement.EnumerateArray().ToArray();
            if (cells.Length == 1)
            {
                var namespaceName = cells[0].GetProperty("kubernetesNamespace").GetString()
                                    ?? throw new InvalidOperationException("Runtime cell has no Kubernetes namespace.");
                var namespaceResource = await ReadNamespaceIfPresentAsync(namespaceName, cancellationToken);
                if (namespaceResource is not null)
                {
                    VerifyOwnedNamespace(namespaceResource);
                    _kubernetesNamespaces[resourceGroupId] = new RecordedNamespace(
                        namespaceName,
                        namespaceResource.Metadata.Uid);
                    return;
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }

        throw new TimeoutException($"A runtime-cell namespace for resource group '{resourceGroupId}' was not created.");
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
        return _kubernetesNamespaces.TryGetValue(resourceGroupId, out var recorded)
            ? recorded.Name
            : throw new InvalidOperationException(
                $"No runtime-cell namespace was recorded for resource group '{resourceGroupId}'.");
    }

    private async Task<V1Namespace?> ReadNamespaceIfPresentAsync(
        string namespaceName,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _kubernetes.CoreV1.ReadNamespaceAsync(
                namespaceName,
                cancellationToken: cancellationToken);
        }
        catch (HttpOperationException exception) when (exception.Response?.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private void VerifyOwnedNamespace(V1Namespace namespaceResource, string? expectedUid = null)
    {
        var labels = namespaceResource.Metadata.Labels;
        if ((expectedUid is not null &&
             !string.Equals(namespaceResource.Metadata.Uid, expectedUid, StringComparison.Ordinal)) ||
            labels is null ||
            !labels.TryGetValue(TestRunIdentity.OwnerLabel, out var owner) ||
            !string.Equals(owner, TestRunIdentity.Owner, StringComparison.Ordinal) ||
            !labels.TryGetValue(TestRunIdentity.WorktreeLabel, out var worktreeId) ||
            !string.Equals(worktreeId, _identity.WorktreeId, StringComparison.Ordinal) ||
            !labels.TryGetValue(TestRunIdentity.RunLabel, out var runId) ||
            !string.Equals(runId, _identity.RunId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Kubernetes namespace '{namespaceResource.Metadata.Name}' does not match the E2E test ownership contract.");
        }
    }

    private async Task StartLabServiceAccessAsync(CancellationToken cancellationToken)
    {
        if (!string.Equals(_lab.State.Profile, "full", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The Aspire E2E suite requires Kubernetes lab profile 'full', but '{_lab.State.Profile}' is active.");
        }

        _harborCredentials = await ReadHarborCredentialsAsync(
            _labServices.Harbor.CredentialsPath,
            cancellationToken);
        _harborPortForward = await KubernetesPortForwardSession.StartAsync(
            _serviceTarget,
            _labServices.Harbor.Namespace,
            $"service/{_labServices.Harbor.Service}",
            _labServices.Harbor.Port,
            cancellationToken);
        try
        {
            _buildKitPortForward = await KubernetesPortForwardSession.StartAsync(
                _serviceTarget,
                _labServices.BuildKit.Namespace,
                $"service/{_labServices.BuildKit.Service}",
                _labServices.BuildKit.Port,
                cancellationToken);
        }
        catch
        {
            await _harborPortForward.DisposeAsync();
            _harborPortForward = null;
            throw;
        }
    }

    private void ConfigureLabServices(
        IResourceBuilder<ProjectResource> api,
        IResourceBuilder<ParameterResource> harborAdminPassword)
    {
        api.WithEnvironment("Harbor__Enabled", "true")
            .WithEnvironment("Harbor__BaseUrl", HarborBaseAddress.ToString())
            .WithEnvironment("Harbor__RegistryHost", _labServices.Harbor.RegistryHost)
            .WithEnvironment("Harbor__Username", _harborCredentials!.Username)
            .WithEnvironment("Harbor__Password", harborAdminPassword)
            .WithEnvironment("Harbor__RobotDurationDays", "-1")
            .WithEnvironment("BuildKit__Enabled", "true")
            .WithEnvironment("BuildKit__Address", BuildKitAddress)
            .WithEnvironment("BuildKit__BuildctlPath", _lab.State.BuildctlPath!)
            .WithEnvironment("BuildKit__TimeoutMinutes", "30");

        foreach (var (key, value) in _identity.NamespaceLabels)
            api.WithEnvironment($"kubernetes__AdditionalNamespaceLabels__{key}", value);
    }

    private void ConfigureLabDeploymentTarget(IResourceBuilder<ProjectResource> api)
    {
        api.WithEnvironment("DeploymentTargets__Bootstrap__Mode", "Kubeconfig")
            .WithEnvironment("DeploymentTargets__Bootstrap__KubeconfigPath", _kubernetesTarget.KubeconfigPath)
            .WithEnvironment("DeploymentTargets__Bootstrap__KubeconfigContext", _kubernetesTarget.Context)
            .WithEnvironment(
                "DeploymentTargets__Bootstrap__WorkloadConfiguration__Ingress__IngressClassName",
                "traefik")
            .WithEnvironment(
                "DeploymentTargets__Bootstrap__WorkloadConfiguration__Ingress__ClusterIssuerName",
                "pole-lab")
            .WithEnvironment(
                "DeploymentTargets__Bootstrap__WorkloadConfiguration__Ingress__PublicBaseDomain",
                _kubernetesTarget.PublicBaseDomain);
    }

    private async Task WaitForRequiredResourceHealthyAsync(
        string resourceName,
        CancellationToken cancellationToken)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            var healthy = _application!.ResourceNotifications.WaitForResourceHealthyAsync(
                resourceName,
                WaitBehavior.StopOnResourceUnavailable,
                wait.Token);
            var terminal = _application.ResourceNotifications.WaitForResourceAsync(
                resourceName,
                KnownResourceStates.TerminalStates,
                wait.Token);
            var completed = await Task.WhenAny(healthy, terminal);
            if (completed == terminal)
            {
                var state = await terminal;
                throw new InvalidOperationException(
                    $"Aspire resource '{resourceName}' reached terminal state '{state}' before becoming healthy.");
            }

            await healthy;
        }
        finally
        {
            await wait.CancelAsync();
        }
    }

    private static async Task<HarborCredentials> ReadHarborCredentialsAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var username = document.RootElement.TryGetProperty("username", out var usernameElement)
            ? usernameElement.GetString()
            : null;
        var password = document.RootElement.TryGetProperty("password", out var passwordElement)
            ? passwordElement.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            throw new InvalidOperationException("The Kubernetes lab Harbor credentials file is malformed.");
        return new HarborCredentials(username, password);
    }

    private static async Task DisposePortForwardAsync(KubernetesPortForwardSession? portForward)
    {
        if (portForward is null)
            return;

        try
        {
            await portForward.DisposeAsync();
        }
        catch
        {
            // The disposable test VM or lab process may already have stopped the forwarding process.
        }
    }

    private Uri HarborBaseAddress => new(
        $"http://127.0.0.1:{_harborPortForward?.LocalPort ?? throw new InvalidOperationException("Harbor access is not active.")}");

    private string BuildKitAddress =>
        $"tcp://127.0.0.1:{_buildKitPortForward?.LocalPort ?? throw new InvalidOperationException("BuildKit access is not active.")}";

    private static string[] CreateAppHostArguments(
        string privateKeyPath,
        string envelopeEncryptionKeyPath,
        string githubClientId,
        string scratchDirectory,
        string kubernetesNamespacePrefix,
        int keycloakPort,
        string keycloakAdminPassword,
        string harborAdminPassword,
        string githubClientSecret,
        string mongoPassword,
        string rabbitMqPassword)
    {
        var arguments = new List<string>
        {
        "E2E:Enabled=true",
        "LocalSourceBuilds:Enabled=false",
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
        $"Parameters:envelope-encryption-active-key-path={envelopeEncryptionKeyPath}",
        $"Parameters:e2e-harbor-admin-password={harborAdminPassword}",
        "ResourceDeployments:InitialRetryDelaySeconds=3600",
        "ResourceDeployments:MaximumRetryDelaySeconds=3600",
        $"E2E:WorkspaceRoot={Path.Combine(scratchDirectory, "source-workspace")}"
        };
        var provider = Environment.GetEnvironmentVariable("POLE_E2E_DATABASE_PROVIDER") ?? "MongoDB";
        arguments.Add($"Database:Provider={provider}");
        if (string.Equals(provider, "Skrynia", StringComparison.OrdinalIgnoreCase))
            arguments.Add($"Database:FilePath={Path.Combine(scratchDirectory, "pole.sqlite")}");

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
            BaseAddress = HarborBaseAddress
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
        if (_buildKitPortForward is null || string.IsNullOrWhiteSpace(_lab.State.BuildctlPath))
            return false;

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _lab.State.BuildctlPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add("--addr");
        process.StartInfo.ArgumentList.Add(BuildKitAddress);
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

    private async Task<int> RunKubectlAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var target = _kubernetesTarget;
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(target.KubectlPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        foreach (var argument in target.KubectlSelectionArguments)
            process.StartInfo.ArgumentList.Add(argument);
        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(cancellationToken);
        await Task.WhenAll(standardOutput, standardError);
        return process.ExitCode;
    }

    private async Task<JsonElement> ReadKubectlJsonAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var target = _kubernetesTarget;
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(target.KubectlPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        foreach (var argument in target.KubectlSelectionArguments)
            process.StartInfo.ArgumentList.Add(argument);
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

internal sealed record HarborCredentials(string Username, string Password);

internal sealed record RecordedNamespace(string Name, string Uid);
