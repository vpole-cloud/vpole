using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
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
using Pole.Features.Core.Kubernetes;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Testing;
using Projects;
using Xunit;

namespace Pole.Tests.E2E.Infrastructure;

public sealed partial class PoleE2EFixture : IAsyncLifetime
{
    private const string KeycloakAdminUsername = "pole-e2e-admin";
    private readonly List<(string TimingPhase, Func<CancellationToken, Task> Action)> _cleanup = [];
    private readonly string _githubClientSecret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private readonly string _keycloakAdminPassword = $"E2e-{Guid.NewGuid():N}-1!";
    private readonly TcpListener _keycloakPortReservation = ReserveTcpPort();
    private readonly IKubernetes _kubernetes;
    private readonly string _kubernetesNamespacePrefix;
    private readonly Dictionary<string, RecordedNamespace> _kubernetesNamespaces = new(StringComparer.Ordinal);
    private readonly KubernetesLabTarget _kubernetesTarget;
    private readonly SelectedKubernetesLab _lab;
    private readonly KubernetesLabServices _labServices;
    private readonly string _mongoPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private readonly string _rabbitMqPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private readonly List<TcpListener> _realtimeReplicaPortReservations = [];
    private readonly KubernetesLabTarget _serviceTarget;
    private readonly CancellationTokenSource _shutdown = new(TimeSpan.FromMinutes(10));
    private readonly string _stubControlKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private readonly TestTimings _timings = new(typeof(PoleE2EFixture), fixture: true);
    private DistributedApplication? _application;
    private KubernetesPortForwardSession? _buildKitPortForward;
    private HarborCredentials? _harborCredentials;
    private KubernetesPortForwardSession? _harborPortForward;
    private bool _harborProjectCleanupCompleted;
    private bool _harborProjectCleanupRegistered;
    private KeycloakAdminClient? _keycloak;
    private bool _realtimeRedisStopped;
    private bool _realtimeReplicaStarted;
    private bool _realtimeReplicaStopped;
    private string? _scratchDirectory;
    private int? _stoppedBuildKitLocalPort;

    public PoleE2EFixture()
    {
        _lab = KubernetesLabSelection.RequireSelection();
        _kubernetesTarget = _lab.Target;
        _labServices = _lab.State.Services ?? throw new InvalidOperationException(
            "The Aspire E2E suite requires a full Kubernetes lab with Harbor and BuildKit services.");
        if (_labServices.Harbor.TargetKey != _labServices.BuildKit.TargetKey)
            throw new InvalidOperationException("Harbor and BuildKit must belong to the same Kubernetes lab target.");
        _serviceTarget = _lab.State.GetRequiredTarget(_labServices.Harbor.TargetKey);
        _kubernetes = new Kubernetes(_kubernetesTarget.CreateConfiguration());
        // RuntimeCellNamespaceNameGenerator appends stable tenant/scope identities. Keep the
        // worktree at the front so recovery can also identify PVC-backed PVs after a namespace
        // has already disappeared.
        _kubernetesNamespacePrefix = $"pole-{Identity.WorktreeId}";
    }

    public string GitHubWebhookSecret { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    public E2eApiClient Api { get; private set; } = null!;

    public HttpClient Stub { get; private set; } = null!;

    public Uri FrontendBaseAddress { get; private set; } = null!;

    public Uri ApiBaseAddress { get; private set; } = null!;

    public TestUser Alpha { get; private set; } = null!;

    public TestUser Beta { get; private set; } = null!;

    public TestUser PlatformAdminUser { get; private set; } = null!;

    public string PlatformAdminAccessToken { get; private set; } = null!;

    public KubernetesLabState LabState => _lab.State;
    public TestRunIdentity Identity { get; } = TestRunIdentity.Create();

    private Uri HarborBaseAddress =>
        new(
            $"http://127.0.0.1:{_harborPortForward?.LocalPort ?? throw new InvalidOperationException("Harbor access is not active.")}");

    private string BuildKitAddress =>
        $"tcp://127.0.0.1:{_buildKitPortForward?.LocalPort ?? throw new InvalidOperationException("BuildKit access is not active.")}";

    private int KeycloakPort => ((IPEndPoint)_keycloakPortReservation.LocalEndpoint).Port;

    public async Task InitializeAsync()
    {
        using var timing = _timings.Measure("fixture.e2e.initialize");
        var evidence = new TestPhaseJournal("E2E fixture initialization");
        using var activation = evidence.Activate();
        try
        {
            evidence.Record(TestPhase.Discovery);
            await InitializeCoreAsync();
            evidence.Record(TestPhase.Completed);
        }
        catch
        {
            evidence.Record(TestPhase.Failed);
            throw;
        }
    }

    public async Task DisposeAsync()
    {
        using var timing = _timings.Measure("fixture.e2e.dispose");
        var evidence = new TestPhaseJournal("E2E fixture disposal");
        using var activation = evidence.Activate();
        evidence.Record(TestPhase.Cleanup);
        var failures = new List<Exception>();

        async Task AttemptAsync(
            string phase,
            string timingPhase,
            Func<Task> action)
        {
            using var actionTiming = _timings.Measure(timingPhase);
            try
            {
                await action();
            }
            catch (Exception failure)
            {
                failures.Add(new InvalidOperationException($"Fixture cleanup phase '{phase}' failed.", failure));
            }
        }

        using (_timings.Measure("fixture.e2e.cleanup.port-reservations"))
        {
            _keycloakPortReservation.Stop();
            foreach (var reservation in _realtimeReplicaPortReservations)
                reservation.Stop();
        }

        using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        using (_timings.Measure("fixture.e2e.cleanup.external"))
        {
            foreach (var action in _cleanup.AsEnumerable().Reverse())
                await AttemptAsync(
                    "restore external fixture state",
                    action.TimingPhase,
                    () => action.Action(cleanupTimeout.Token));
        }

        if (_application is not null)
        {
            using var applicationTiming = _timings.Measure("fixture.e2e.cleanup.application");
            await AttemptAsync("cancel startup", "fixture.e2e.cleanup.startup.cancel", () => _shutdown.CancelAsync());
            using var stopTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
            await AttemptAsync(
                "stop application",
                "fixture.e2e.cleanup.application.stop",
                () => _application.StopAsync(stopTimeout.Token));
            // Dispose remains mandatory even when Stop fails.
            await AttemptAsync(
                "dispose application",
                "fixture.e2e.cleanup.application.dispose",
                () => _application.DisposeAsync().AsTask());
        }

        await StopLifecycleEvidenceAsync();

        using var gitOpsCleanupTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using var gitOpsTiming = _timings.Measure("fixture.e2e.cleanup.gitops");
        await AttemptAsync(
            "remove owned GitOps namespaces",
            "fixture.e2e.cleanup.gitops.namespaces",
            () => CleanupGitOpsNamespacesAsync(gitOpsCleanupTimeout.Token));
        await AttemptAsync("remove GitOps test images", "fixture.e2e.cleanup.gitops.images", DisposeGitOpsImagesAsync);
        gitOpsTiming.Dispose();

        await AttemptAsync(
            "dispose BuildKit forwarding",
            "fixture.e2e.cleanup.buildkit.forward.dispose",
            () => DisposePortForwardAsync(_buildKitPortForward));
        _buildKitPortForward = null;
        await AttemptAsync(
            "dispose Harbor forwarding",
            "fixture.e2e.cleanup.harbor.forward.dispose",
            () => DisposePortForwardAsync(_harborPortForward));
        _harborPortForward = null;
        await AttemptAsync(
            "dispose Kubernetes client",
            "fixture.e2e.cleanup.kubernetes-client.dispose",
            () =>
            {
                _kubernetes.Dispose();
                return Task.CompletedTask;
            });
        await AttemptAsync(
            "dispose API client",
            "fixture.e2e.cleanup.api-client.dispose",
            () =>
            {
                Api?.Dispose();
                return Task.CompletedTask;
            });
        await AttemptAsync(
            "dispose startup budget",
            "fixture.e2e.cleanup.startup-budget.dispose",
            () =>
            {
                _shutdown.Dispose();
                return Task.CompletedTask;
            });
        await AttemptAsync(
            "remove scratch files",
            "fixture.e2e.cleanup.scratch.remove",
            () =>
            {
                if (!string.IsNullOrWhiteSpace(_scratchDirectory) && Directory.Exists(_scratchDirectory))
                    Directory.Delete(_scratchDirectory, recursive: true);
                return Task.CompletedTask;
            });
        if (_harborProjectCleanupRegistered && !_harborProjectCleanupCompleted)
            failures.Add(
                new InvalidOperationException("The source-build E2E Harbor project cleanup did not complete."));
        if (failures.Count > 0)
        {
            evidence.Record(TestPhase.Failed);
            throw new AggregateException("E2E fixture cleanup failed.", failures);
        }

        evidence.Record(TestPhase.Completed);
    }

    private async Task InitializeCoreAsync()
    {
        await StartLabServiceAccessAsync(_shutdown.Token);
        using var credentialsTiming = _timings.Measure("fixture.e2e.credentials.prepare");
        _scratchDirectory = Path.Combine(Path.GetTempPath(), "pole-e2e", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_scratchDirectory);
        var privateKeyPath = Path.Combine(_scratchDirectory, "github-app-private-key.pem");
        using (var rsa = RSA.Create(2048))
        {
            await File.WriteAllTextAsync(privateKeyPath, rsa.ExportPkcs8PrivateKeyPem(), _shutdown.Token);
        }

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

        credentialsTiming.Dispose();
        using var graphTiming = _timings.Measure("fixture.e2e.graph.construct");
        var githubClientId = $"pole-e2e-client-{Guid.NewGuid():N}";
        var appHost = await DistributedApplicationTestingBuilder.CreateAsync<Pole_AppHost>(
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
        // Browser scenarios explicitly exercise native provisioning. Operator-specific manifests
        // and credential projections have separate owning feature tests in the full lab.
        var githubStub = appHost
            .AddProject(
                "github-stub",
                "../../../tests/Pole.Tests.E2E.GitHubStub/Pole.Tests.E2E.GitHubStub.csproj",
                launchProfileName: null)
            .WithHttpEndpoint(name: "http", env: "ASPNETCORE_HTTP_PORTS")
            .WithHttpHealthCheck("/healthz", endpointName: "http");

        api.WithEnvironment("GitHubApp__Enabled", "true")
            .WithEnvironment("GitHubWebhook__Secret", GitHubWebhookSecret)
            .WithEnvironment("GitHubApp__ClientId", githubClientId)
            .WithEnvironment("GitHubApp__ClientSecret", _githubClientSecret)
            .WithEnvironment("GitHubApp__AppSlug", "pole-e2e")
            .WithEnvironment("GitHubApp__PrivateKeyPath", privateKeyPath)
            .WithEnvironment("GitHubApp__ApiBaseUrl", githubStub.GetEndpoint("http"))
            .WithEnvironment("GitHubApp__WebBaseUrl", githubStub.GetEndpoint("http"))
            .WithEnvironment("GitHubApp__PublicBaseUrl", api.GetEndpoint("http"))
            .WithEnvironment("GitHubApp__FrontendBaseUrl", frontend.GetEndpoint("http"));
        githubStub.WithEnvironment("GitHubStub__CallbackBaseUrl", api.GetEndpoint("http"))
            .WithEnvironment("GitHubStub__ControlKey", _stubControlKey);
        ConfigureRealtimeReplica(appHost, api);
        ConfigureOwnedContainerResources(appHost);

        var startupResourceNames = appHost.Resources
            .Where(resource => resource is not ParameterResource)
            .Select(resource => resource.Name)
            .Order(StringComparer.Ordinal)
            .Take(32)
            .ToArray();
        graphTiming.Dispose();
        using (_timings.Measure("fixture.e2e.graph.build"))
        {
            _application = await appHost.BuildAsync(_shutdown.Token);
        }

        _keycloakPortReservation.Stop();
        using (var startup = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token))
        {
            using var mongoWatch = CancellationTokenSource.CreateLinkedTokenSource(startup.Token);

            async Task StartApplicationAsync()
            {
                using var timing = _timings.Measure("fixture.e2e.graph.start");
                await _application!.StartAsync(startup.Token);
            }

            async Task WaitForServerAsync()
            {
                using var timing = _timings.Measure("fixture.e2e.server.ready");
                await WaitForRequiredResourceHealthyAsync("pole-server", startup.Token);
            }

            var starting = StartApplicationAsync();
            var mongoExited = _application.ResourceNotifications.WaitForResourceAsync(
                "pole-mongo",
                KnownResourceStates.TerminalStates,
                mongoWatch.Token);
            var serverReady = WaitForServerAsync();
            try
            {
                using (_timings.Measure("fixture.e2e.graph.startup.wait"))
                {
                    await WaitForStartupAsync(starting, serverReady, mongoExited);
                }
            }
            catch (Exception exception)
            {
                await startup.CancelAsync();
                // Observe all tasks after cancellation before disposing the application.
                try
                {
                    await Task.WhenAll(starting, mongoExited, serverReady);
                }
                catch
                {
                    // The original failure is reported below.
                }

                throw new InvalidOperationException(
                    $"Aspire startup failed. {DescribeStartupStates(startupResourceNames)} " +
                    await GetMongoStartupErrorsAsync(),
                    exception);
            }
            finally
            {
                await mongoWatch.CancelAsync();
                try
                {
                    await mongoExited;
                }
                catch (OperationCanceledException) when (mongoWatch.IsCancellationRequested)
                {
                }
            }
        }

        StartLifecycleEvidence();
        using (_timings.Measure("fixture.e2e.frontend.ready"))
        {
            await WaitForRequiredResourceHealthyAsync("frontend", _shutdown.Token);
        }

        using (_timings.Measure("fixture.e2e.github-stub.ready"))
        {
            await WaitForRequiredResourceHealthyAsync("github-stub", _shutdown.Token);
        }

        using var apiClient = _application.CreateHttpClient("pole-server", "http");
        ApiBaseAddress = apiClient.BaseAddress ??
            throw new InvalidOperationException("Aspire did not resolve the API endpoint.");
        // Ordinary fixture HTTP authenticates each request with its user's bearer.
        // Hub scenarios own independent cookie jars, including different accounts.
        Api = new E2eApiClient(
            new HttpClient(new HttpClientHandler { UseCookies = false })
            {
                BaseAddress = ApiBaseAddress,
            });
        Stub = _application.CreateHttpClient("github-stub", "http");
        Stub.DefaultRequestHeaders.Add("X-Pole-E2E-Stub-Key", _stubControlKey);
        FrontendBaseAddress = _application.CreateHttpClient("frontend", "http").BaseAddress ??
            throw new InvalidOperationException("Aspire did not resolve the frontend endpoint.");
        _keycloak = new KeycloakAdminClient(
            _application.CreateHttpClient("pole-keycloak", "http"),
            KeycloakAdminUsername,
            _keycloakAdminPassword);
        using (_timings.Measure("fixture.e2e.keycloak.discovery.ready"))
        {
            await WaitForKeycloakDiscoveryAsync(
                _application.CreateHttpClient("pole-keycloak", "http"),
                _shutdown.Token);
        }

        using var realmTiming = _timings.Measure("fixture.e2e.keycloak.realm.configure");
        await _keycloak.SetRealmAccessTokenLifespanAsync(TimeSpan.FromHours(1), _shutdown.Token);
        await ConfigureInteractiveClientAsync(_shutdown.Token);
        var directGrantClient = await _keycloak.CreateDirectGrantClientAsync(
            $"pole.e2e.direct-{Guid.NewGuid():N}",
            _shutdown.Token);
        _cleanup.Add(
            ("fixture.e2e.cleanup.keycloak.direct-client.delete",
                ct => _keycloak.DeleteClientAsync(directGrantClient.InternalId, ct)));

        realmTiming.Dispose();
        using var usersTiming = _timings.Measure("fixture.e2e.users.prepare");
        var bootstrap = await CreateBootstrapUserAsync(_shutdown.Token);
        await _keycloak.AssignRealmRoleAsync(bootstrap.KeycloakUserId, "pole-platform-admin", _shutdown.Token);
        var bootstrapToken = await _keycloak.GetPasswordGrantTokenAsync(
            directGrantClient.ClientId,
            directGrantClient.Secret,
            bootstrap.Email,
            bootstrap.Password,
            _shutdown.Token);
        PlatformAdminAccessToken = bootstrapToken;
        PlatformAdminUser = bootstrap with
        {
            AccessToken = bootstrapToken,
        };
        RegisterApiToken(bootstrapToken, bootstrap.Email, bootstrap.Password, directGrantClient);
        await AllowBootstrapTargetWithoutEnforcementAsync(_shutdown.Token);

        Alpha = await CreatePlatformUserAsync("alpha", bootstrapToken, directGrantClient, _shutdown.Token);
        Beta = await CreatePlatformUserAsync("beta", bootstrapToken, directGrantClient, _shutdown.Token);
    }

    private void ConfigureRealtimeReplica(
        IDistributedApplicationBuilder appHost,
        IResourceBuilder<ProjectResource> primary)
    {
        var ports = Enumerable.Range(0, 4)
            .Select(_ =>
            {
                var reservation = ReserveTcpPort();
                _realtimeReplicaPortReservations.Add(reservation);
                return ((IPEndPoint)reservation.LocalEndpoint).Port;
            })
            .ToArray();
        var projectDirectory = Path.GetDirectoryName(new Pole_Server().ProjectPath) ??
            throw new InvalidOperationException("The API project directory is unavailable.");
        var testOutput = new FileInfo(typeof(PoleE2EFixture).Assembly.Location).Directory ??
            throw new InvalidOperationException("The E2E build output directory is unavailable.");
        var configuration = testOutput.Parent?.Name ??
            throw new InvalidOperationException("The E2E build configuration is unavailable.");
        var serverAssembly = Path.Combine(projectDirectory, "bin", configuration, testOutput.Name, "Pole.Server.dll");
        if (!File.Exists(serverAssembly))
            throw new InvalidOperationException(
                "The matching built API assembly is unavailable; build the E2E project before running it.");
        // The E2E build already includes the full API. Launch that same artifact directly
        // so DCP supervises the API process without a dotnet-run parent and its process tree.
        var replica = appHost.AddExecutable("pole-server-replica", "dotnet", projectDirectory, "exec", serverAssembly)
            .WithExplicitStart()
            .WithHttpEndpoint(port: ports[0], targetPort: ports[0], name: "http", isProxied: false)
            .WithHttpEndpoint(port: ports[1], targetPort: ports[1], name: "functions-public", isProxied: false)
            .WithHttpEndpoint(port: ports[2], targetPort: ports[2], name: "functions-internal", isProxied: false)
            .WithHttpHealthCheck("/health", endpointName: "http")
            .WaitFor(primary);
        // Reuse the complete test graph's service references and protected key files. Listener
        // and silo addresses belong to each process and are overridden after these callbacks.
        foreach (var environment in primary.Resource.Annotations.OfType<EnvironmentCallbackAnnotation>())
            replica.Resource.Annotations.Add(environment);
        replica.WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
            // The pinned DCP stop command retries for about 15s. Bound this owned
            // replica's graceful shutdown below that window; still require its real
            // terminal state, same-instance stop timestamp and accepted exit status.
            .WithEnvironment("DOTNET_SHUTDOWNTIMEOUTSECONDS", "10")
            .WithEnvironment(
                "ASPNETCORE_URLS",
                string.Join(';', ports.Take(3).Select(port => $"http://localhost:{port}")))
            .WithEnvironment("Functions__Gateway__ControlPort", ports[0].ToString(CultureInfo.InvariantCulture))
            .WithEnvironment("Functions__Gateway__PublicPort", ports[1].ToString(CultureInfo.InvariantCulture))
            .WithEnvironment("Functions__Gateway__InternalPort", ports[2].ToString(CultureInfo.InvariantCulture))
            .WithEnvironment("Functions__Orleans__SiloPort", ports[3].ToString(CultureInfo.InvariantCulture))
            .WithEnvironment("Functions__Orleans__GatewayPort", "0");
    }

    public async Task<Uri> StartRealtimeReplicaAsync(CancellationToken cancellationToken)
    {
        var application
            = _application ?? throw new InvalidOperationException("The E2E application is not initialized.");
        foreach (var reservation in _realtimeReplicaPortReservations)
            reservation.Stop();
        using var command = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        command.CancelAfter(TimeSpan.FromSeconds(60));
        var intent = DateTimeOffset.UtcNow;
        var before = GetRealtimeReplicaLifecycleSnapshot();
        _realtimeReplicaStopped = false;
        var result = await application.ResourceCommands.ExecuteCommandAsync(
            "pole-server-replica",
            KnownResourceCommands.StartCommand,
            command.Token);
        if (!result.Success)
            throw new InvalidOperationException(
                $"The realtime API replica could not start: commandSucceeded={result.Success}, canceled={result.Canceled}, intentUtc={intent:O}. Before: {before}. Current: {GetRealtimeReplicaLifecycleSnapshot()}");
        // Restart may initially expose the previous instance's terminal/health snapshot.
        try
        {
            await application.ResourceNotifications.WaitForResourceAsync(
                "pole-server-replica",
                KnownResourceStates.Running,
                command.Token);
            await WaitForRequiredResourceHealthyAsync("pole-server-replica", command.Token);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"The realtime API replica did not become running and healthy: commandSucceeded={result.Success}, canceled={result.Canceled}, budgetCanceled={cancellationToken.IsCancellationRequested}, intentUtc={intent:O}. Before: {before}. Current: {GetRealtimeReplicaLifecycleSnapshot()}",
                exception);
        }

        _realtimeReplicaStarted = true;
        using var client = application.CreateHttpClient("pole-server-replica", "http");
        return client.BaseAddress ??
            throw new InvalidOperationException("Aspire did not resolve the realtime API replica endpoint.");
    }

    public async Task StopRealtimeReplicaAsync(CancellationToken cancellationToken)
    {
        if (_application is null || _realtimeReplicaStopped)
            return;
        const string name = "pole-server-replica";
        if (!_application.ResourceNotifications.TryGetCurrentState(name, out var before))
            throw new InvalidOperationException("The realtime API replica state is unavailable before cleanup.");
        if (!_realtimeReplicaStarted && before.Snapshot.State?.Text is "NotStarted" or "FailedToStart")
            return;
        if (before.Snapshot.State?.Text is not "Running")
            throw new InvalidOperationException(
                $"The realtime API replica was not running before its owned stop. {GetRealtimeReplicaLifecycleSnapshot()}");
        var intent = DateTimeOffset.UtcNow;
        var beforeStop = GetRealtimeReplicaLifecycleSnapshot();
        var result = await _application.ResourceCommands.ExecuteCommandAsync(
            before.Resource.Name,
            KnownResourceCommands.StopCommand,
            cancellationToken);
        var retryExhausted = result.Message?.Contains("Failed to stop resource", StringComparison.Ordinal) == true;
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        wait.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            // DCP's stop command has a shorter retry window than a graceful Orleans shutdown.
            // A command result alone does not prove that the owned process actually exited.
            await _application.ResourceNotifications.WaitForResourceAsync(
                name,
                [KnownResourceStates.Finished, KnownResourceStates.Exited, "Terminated"],
                wait.Token);
        }
        catch (OperationCanceledException exception)
        {
            throw new InvalidOperationException(
                $"The realtime API replica did not finish its owned stop: commandSucceeded={result.Success}, canceled={result.Canceled}, retryExhausted={retryExhausted}, budgetCanceled={cancellationToken.IsCancellationRequested}, intentUtc={intent:O}. Before: {beforeStop}. Current: {GetRealtimeReplicaLifecycleSnapshot()}",
                exception);
        }

        if (!_application.ResourceNotifications.TryGetCurrentState(name, out var after))
            throw new InvalidOperationException("The realtime API replica state is unavailable after cleanup.");
        var controlledStop = after.Snapshot.StartTimeStamp == before.Snapshot.StartTimeStamp &&
            after.Snapshot.StopTimeStamp is { } stoppedAt && stoppedAt >= intent;
        // Aspire 13.5.4 uses -1 for an unavailable OS exit status. Accept that sentinel
        // only after a successful stop of this same instance; actual failure codes stay failures.
        var acceptedExit = after.Snapshot.ExitCode == 0 || (result.Success && after.Snapshot.ExitCode == -1);
        if (!controlledStop || !acceptedExit || result.Canceled || (!result.Success && !retryExhausted))
            throw new InvalidOperationException(
                $"The realtime API replica stop was not verified: commandSucceeded={result.Success}, canceled={result.Canceled}, retryExhausted={retryExhausted}, intentUtc={intent:O}. Before: {beforeStop}. Current: {GetRealtimeReplicaLifecycleSnapshot()}");
        _realtimeReplicaStopped = true;
    }

    public string GetRealtimeReplicaLifecycleSnapshot()
    {
        const string name = "pole-server-replica";
        // Report only fixed resource identity and lifecycle scalars. Never serialize a
        // snapshot, resource configuration, command message, endpoint, environment or log.
        try
        {
            if (_application is null || !_application.ResourceNotifications.TryGetCurrentState(name, out var resource))
                return
                    $"{name}: snapshot=unavailable, started={_realtimeReplicaStarted}, verifiedStop={_realtimeReplicaStopped}";
            var snapshot = resource.Snapshot;
            var observedState = snapshot.State?.Text;
            var state = observedState switch
            {
                "Starting" or "Running" or "FailedToStart" or "RuntimeUnhealthy" or "Waiting" or "NotStarted"
                    or "Building" or "Stopping" or "Exited" or "Finished" or "Terminated" or "Unknown" => observedState,
                null => "none",
                _ => "Other",
            };
            var instance = resource.ResourceId is { } id &&
                (id == name || id.StartsWith(name + "-", StringComparison.Ordinal)) &&
                Regex.IsMatch(id, "^[A-Za-z0-9_-]{1,200}$")
                    ? id
                    : "unexpected";
            return
                $"{name}: instance={instance}, sameLogicalResource={resource.Resource.Name == name}, state={state}, health={snapshot.HealthStatus?.ToString() ?? "none"}, startUtc={snapshot.StartTimeStamp?.ToString("O") ?? "none"}, stopUtc={snapshot.StopTimeStamp?.ToString("O") ?? "none"}, exit={snapshot.ExitCode?.ToString() ?? "none"}, started={_realtimeReplicaStarted}, verifiedStop={_realtimeReplicaStopped}";
        }
        catch
        {
            return $"{name}: snapshot=unavailable";
        }
    }

    public async Task StopRealtimeRedisAsync(CancellationToken cancellationToken)
    {
        var application
            = _application ?? throw new InvalidOperationException("The E2E application is not initialized.");
        const string name = "realtime";
        if (_realtimeRedisStopped)
            return;
        if (!application.ResourceNotifications.TryGetCurrentState(name, out var before) ||
            before.Resource is not ContainerResource || before.Snapshot.State?.Text != KnownResourceStates.Running)
            throw new InvalidOperationException(
                "The fixture-owned realtime Redis is not running before its controlled outage.");
        // Register caller cleanup first. Mark intent before the command so a partial stop also
        // gets restored. Only this fixture's session-scoped graph resource can be controlled.
        _realtimeRedisStopped = true;
        using var command = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        command.CancelAfter(TimeSpan.FromSeconds(30));
        var result = await application.ResourceCommands.ExecuteCommandAsync(
            before.Resource.Name,
            KnownResourceCommands.StopCommand,
            command.Token);
        if (!result.Success)
            throw new InvalidOperationException("The fixture-owned realtime Redis stop command failed.");
        await application.ResourceNotifications.WaitForResourceAsync(
            name,
            [KnownResourceStates.Finished, KnownResourceStates.Exited, "Terminated"],
            command.Token);
        if (!application.ResourceNotifications.TryGetCurrentState(name, out var after) ||
            !ReferenceEquals(after.Resource, before.Resource) ||
            after.Snapshot.StartTimeStamp != before.Snapshot.StartTimeStamp)
            throw new InvalidOperationException(
                "The fixture-owned realtime Redis stop did not retain its controlled resource identity.");
    }

    public async Task StartRealtimeRedisAsync(CancellationToken cancellationToken)
    {
        if (!_realtimeRedisStopped)
            return;
        var application
            = _application ?? throw new InvalidOperationException("The E2E application is not initialized.");
        const string name = "realtime";
        using var command = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        command.CancelAfter(TimeSpan.FromSeconds(45));
        if (!application.ResourceNotifications.TryGetCurrentState(name, out var current))
            throw new InvalidOperationException(
                "The fixture-owned realtime Redis state is unavailable during recovery.");
        if (current.Snapshot.State?.Text == "Stopping")
            await application.ResourceNotifications.WaitForResourceAsync(
                name,
                [KnownResourceStates.Finished, KnownResourceStates.Exited, "Terminated"],
                command.Token);
        if (current.Snapshot.State?.Text != KnownResourceStates.Running)
        {
            var result = await application.ResourceCommands.ExecuteCommandAsync(
                name,
                KnownResourceCommands.StartCommand,
                command.Token);
            if (!result.Success)
                throw new InvalidOperationException("The fixture-owned realtime Redis recovery command failed.");
            await application.ResourceNotifications.WaitForResourceAsync(
                name,
                KnownResourceStates.Running,
                command.Token);
        }

        await WaitForRequiredResourceHealthyAsync(name, command.Token);
        _realtimeRedisStopped = false;
    }

    private void ConfigureOwnedContainerResources(IDistributedApplicationBuilder appHost)
    {
        var labelArguments = Identity.NamespaceLabels
            .SelectMany(label => new[] { "--label", $"{label.Key}={label.Value}" })
            .ToArray();
        foreach (var resource in appHost.Resources.OfType<ContainerResource>())
            appHost.CreateResourceBuilder(resource).WithContainerRuntimeArgs(labelArguments);
    }

    public HttpRequestMessage CreateAuthenticatedRequest(
        HttpMethod method,
        string path,
        TestUser user,
        string? tenantId = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", user.AccessToken);
        if (!string.IsNullOrWhiteSpace(tenantId))
            request.Headers.Add("X-Pole-Tenant-ID", tenantId);
        return request;
    }

    public HttpRequestMessage CreatePlatformAdminRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", PlatformAdminAccessToken);
        return request;
    }

    public async Task<JsonElement> AllowTargetWithoutEnforcementAsync(
        string targetId,
        long revision,
        CancellationToken cancellationToken)
    {
        // Workload E2E does not verify CNI enforcement; explicitly opt out for this test target.
        using var request = CreatePlatformAdminRequest(
            HttpMethod.Put,
            $"/api/v1/platform/deployment-targets/{targetId}/network-policy");
        request.Content = JsonContent.Create(
            new
            {
                enforcement = "Unknown",
                allowIsolationNotEnforced = true,
                revision,
            });
        using var response = await Api.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
    }

    private async Task AllowBootstrapTargetWithoutEnforcementAsync(CancellationToken cancellationToken)
    {
        using var request = CreatePlatformAdminRequest(HttpMethod.Get, "/api/v1/platform/deployment-targets/");
        using var response = await Api.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var targets = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        var target = targets.EnumerateArray().Single(item => item.GetProperty("isDefault").GetBoolean());
        await AllowTargetWithoutEnforcementAsync(
            target.GetProperty("id").GetString() ??
            throw new InvalidOperationException("The bootstrap deployment target has no ID."),
            target.GetProperty("revision").GetInt64(),
            cancellationToken);
    }

    public async Task EnsureSourceBuildInfrastructureReadyAsync(CancellationToken cancellationToken)
    {
        await WaitForBuildKitReadyAsync(cancellationToken);
        await WaitForHarborHealthAsync(cancellationToken);
        if (!_harborProjectCleanupRegistered)
        {
            _cleanup.Add(
                ("fixture.e2e.cleanup.harbor.project.delete", async token =>
                {
                    await DeleteHarborProjectAsync(Alpha.DefaultTenantId, token);
                    _harborProjectCleanupCompleted = true;
                }));
            _harborProjectCleanupRegistered = true;
        }
    }

    public async Task StopBuildKitAccessAsync()
    {
        var portForward = Interlocked.Exchange(ref _buildKitPortForward, null) ??
            throw new InvalidOperationException("BuildKit access has already been stopped.");
        _stoppedBuildKitLocalPort = portForward.LocalPort;
        await portForward.DisposeAsync();
    }

    public async Task RestoreBuildKitAccessAsync(CancellationToken cancellationToken)
    {
        var localPort = _stoppedBuildKitLocalPort ??
            throw new InvalidOperationException("BuildKit access has not been stopped.");
        _buildKitPortForward = await KubernetesPortForwardSession.StartAsync(
            _serviceTarget,
            _labServices.BuildKit.Namespace,
            $"service/{_labServices.BuildKit.Service}",
            _labServices.BuildKit.Port,
            cancellationToken,
            localPort);
        _stoppedBuildKitLocalPort = null;
        await WaitForBuildKitReadyAsync(cancellationToken);
    }

    public async Task DeleteHarborProjectAsync(string tenantId, CancellationToken cancellationToken)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(tenantId));
        var projectName = $"pole-{Convert.ToHexStringLower(hash.AsSpan(0, 16))}";
        using var client = new HttpClient(new HttpClientHandler { UseCookies = false })
        {
            BaseAddress = HarborBaseAddress,
        };
        var credentials = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{_harborCredentials!.Username}:{_harborCredentials.Password}"));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        var repositoryNames = new List<string>();
        for (var page = 1;; page++)
        {
            using var repositoriesResponse = await client.GetAsync(
                $"/api/v2.0/projects/{Uri.EscapeDataString(projectName)}/repositories?page={page}&page_size=100",
                cancellationToken);
            if (repositoriesResponse.StatusCode == HttpStatusCode.NotFound)
                return;
            repositoriesResponse.EnsureSuccessStatusCode();
            using var repositories
                = JsonDocument.Parse(await repositoriesResponse.Content.ReadAsStreamAsync(cancellationToken));
            var currentPage = repositories.RootElement.EnumerateArray().ToArray();
            repositoryNames.AddRange(
                currentPage.Select(item => item.GetProperty("name").GetString())
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Select(name => name!));
            if (currentPage.Length < 100)
                break;
        }

        foreach (var repositoryName in repositoryNames)
        {
            var relativeRepositoryName
                = repositoryName.StartsWith($"{projectName}/", StringComparison.OrdinalIgnoreCase)
                    ? repositoryName[(projectName.Length + 1)..]
                    : repositoryName;
            // Harbor treats nested repository names as one route parameter. HttpClient/Uri
            // unescape the first level, so the embedded slash must be encoded twice.
            var encodedRepository = Uri.EscapeDataString(Uri.EscapeDataString(relativeRepositoryName));
            using var deleteRepositoryResponse = await client.DeleteAsync(
                $"/api/v2.0/projects/{Uri.EscapeDataString(projectName)}/repositories/{encodedRepository}",
                cancellationToken);
            if (!deleteRepositoryResponse.IsSuccessStatusCode &&
                deleteRepositoryResponse.StatusCode != HttpStatusCode.NotFound)
                throw new HttpRequestException(
                    $"Harbor did not delete the E2E repository: {(int)deleteRepositoryResponse.StatusCode}.");
        }

        using var deleteProjectResponse = await client.DeleteAsync(
            $"/api/v2.0/projects/{Uri.EscapeDataString(projectName)}",
            cancellationToken);
        if (!deleteProjectResponse.IsSuccessStatusCode && deleteProjectResponse.StatusCode != HttpStatusCode.NotFound)
            throw new HttpRequestException(
                $"Harbor did not delete the E2E project: {(int)deleteProjectResponse.StatusCode}.");
    }

    public async Task<string> GetHarborArtifactDigestAsync(
        string tenantId,
        string webServiceId,
        string imageDigest,
        CancellationToken cancellationToken)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(tenantId));
        var projectName = $"pole-{Convert.ToHexStringLower(hash.AsSpan(0, 16))}";
        // See DeleteHarborProjectAsync: nested repository paths require double encoding.
        var repository = Uri.EscapeDataString(Uri.EscapeDataString($"web-services/{webServiceId}"));
        using var client = new HttpClient(new HttpClientHandler { UseCookies = false })
        {
            BaseAddress = HarborBaseAddress,
        };
        var credentials = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{_harborCredentials!.Username}:{_harborCredentials.Password}"));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        var encodedProject = Uri.EscapeDataString(projectName);
        var encodedDigest = Uri.EscapeDataString(imageDigest);
        using var response = await client.GetAsync(
            $"/api/v2.0/projects/{encodedProject}/repositories/{repository}/artifacts/{encodedDigest}",
            cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        return document.RootElement.GetProperty("digest").GetString() ??
            throw new InvalidOperationException("Harbor returned an artifact without a digest.");
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

    public async Task WaitForNamespaceCreationAsync(
        string resourceGroupId,
        CancellationToken cancellationToken,
        TestUser? owner = null,
        string? tenantId = null)
    {
        owner ??= Alpha;
        await TestWait.UntilAsync(
            async phase =>
            {
                using var request = CreateAuthenticatedRequest(
                    HttpMethod.Get,
                    $"/api/v1/resource-groups/{resourceGroupId}/runtime-cells",
                    owner,
                    tenantId ?? owner.DefaultTenantId);
                using var response = await Api.SendAsync(request, phase);
                response.EnsureSuccessStatusCode();
                using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(phase));
                var cells = document.RootElement.EnumerateArray().ToArray();
                if (cells.Length == 1)
                {
                    var namespaceName = cells[0].GetProperty("kubernetesNamespace").GetString() ??
                        throw new InvalidOperationException("Runtime cell has no Kubernetes namespace.");
                    var namespaceResource = await ReadNamespaceIfPresentAsync(namespaceName, phase);
                    if (namespaceResource is not null)
                    {
                        VerifyOwnedNamespace(namespaceResource);
                        _kubernetesNamespaces[resourceGroupId] = new RecordedNamespace(
                            namespaceName,
                            namespaceResource.Metadata.Uid);
                        return true;
                    }
                }

                return false;
            },
            TimeSpan.FromMinutes(2),
            "The runtime-cell namespace was not created.",
            cancellationToken,
            TimeSpan.FromSeconds(2));
    }


    public Task<JsonElement> GetWebServiceDeploymentAsync(
        string resourceGroupId,
        string webServiceName,
        CancellationToken cancellationToken) => ReadKubectlJsonAsync(
        ["get", "deployment", webServiceName, "--namespace", GetNamespaceName(resourceGroupId), "--output=json"],
        cancellationToken);

    public Task<JsonElement> GetWebServiceConfigMapAsync(
        string resourceGroupId,
        string webServiceName,
        CancellationToken cancellationToken) => ReadKubectlJsonAsync(
        [
            "get", "configmap", $"{webServiceName}-config", "--namespace", GetNamespaceName(resourceGroupId),
            "--output=json",
        ],
        cancellationToken);

    public async Task<bool> KubernetesSecretExistsAsync(
        string resourceGroupId,
        string secretName,
        CancellationToken cancellationToken) => await RunKubectlAsync(
        ["get", "secret", secretName, "--namespace", GetNamespaceName(resourceGroupId)],
        cancellationToken) == 0;

    public async Task<string> CreateOwnedStorageObservationClassAsync(
        TestResourceScope resources,
        string deploymentTargetId,
        string canary,
        CancellationToken ct)
    {
        using var request = CreatePlatformAdminRequest(HttpMethod.Get, "/api/v1/platform/deployment-targets");
        using var response = await Api.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
        var target = document.RootElement
            .EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == deploymentTargetId);
        if (!target.GetProperty("isDefault").GetBoolean() || !target.GetProperty("enabled").GetBoolean())
            throw new InvalidOperationException("Native observation fixtures require their enabled bootstrap target.");

        var created = await _kubernetes.StorageV1.CreateStorageClassAsync(
            new V1StorageClass
            {
                Metadata = new V1ObjectMeta
                {
                    Name = $"pole-observation-{Guid.NewGuid():N}",
                    Labels = Identity.NamespaceLabels.ToDictionary(pair => pair.Key, pair => pair.Value),
                    Annotations = new Dictionary<string, string> { ["pole.io/testing-canary"] = canary },
                },
                // A Kubernetes qualified provisioner may contain a 253-character DNS
                // prefix and a 63-character suffix; it need not be a CSI driver in use.
                Provisioner = string.Join(
                        '.',
                        new[] { new string('a', 63), new string('b', 63), new string('c', 63), new string('d', 61) }) +
                    "/" + new string('e', 63),
                ReclaimPolicy = "Retain",
                VolumeBindingMode = "WaitForFirstConsumer",
                Parameters = new Dictionary<string, string> { ["fake.credentials"] = canary },
            },
            cancellationToken: ct);
        var name = created.Metadata.Name;
        var uid = created.Metadata.Uid;
        resources.Defer(
            "delete exact fixture-owned observation StorageClass",
            async cleanup =>
            {
                V1StorageClass current;
                try
                {
                    current = await _kubernetes.StorageV1.ReadStorageClassAsync(name, cancellationToken: cleanup);
                }
                catch (HttpOperationException exception) when
                    (exception.Response?.StatusCode == HttpStatusCode.NotFound)
                {
                    return;
                }

                if (string.IsNullOrEmpty(uid) || current.Metadata.Uid != uid || Identity.NamespaceLabels.Any(pair =>
                        current.Metadata.Labels?.TryGetValue(pair.Key, out var value) != true || value != pair.Value))
                    throw new InvalidOperationException(
                        "The observation StorageClass no longer matches exact test ownership.");
                await _kubernetes.StorageV1.DeleteStorageClassAsync(
                    name,
                    body: new V1DeleteOptions { Preconditions = new V1Preconditions { Uid = uid } },
                    cancellationToken: cleanup);
            });
        return name;
    }

    public async Task<IReadOnlyList<OwnedObservationPod>> ListOwnedObservationPodsAsync(
        TestUser user,
        string tenantId,
        string serviceId,
        string deploymentId,
        CancellationToken cancellationToken)
    {
        var context = await ObservationPodContextAsync(user, tenantId, serviceId, deploymentId, cancellationToken);
        return await OwnedObservationPodsAsync(context, cancellationToken);
    }

    public async Task DeleteOwnedObservationPodAsync(
        TestUser user,
        string tenantId,
        string serviceId,
        string deploymentId,
        string podUid,
        CancellationToken cancellationToken)
    {
        var context = await ObservationPodContextAsync(user, tenantId, serviceId, deploymentId, cancellationToken);
        var pod
            = (await OwnedObservationPodsAsync(context, cancellationToken))
            .SingleOrDefault(item => item.Uid == podUid) ??
            throw new InvalidOperationException("The requested pod is not owned by the current test workload.");
        await _kubernetes.CoreV1.DeleteNamespacedPodAsync(
            pod.Name,
            context.Namespace,
            body: new V1DeleteOptions { GracePeriodSeconds = 0, Preconditions = new V1Preconditions { Uid = pod.Uid } },
            cancellationToken: cancellationToken);
    }

    private async Task<ObservationPodContext> ObservationPodContextAsync(
        TestUser user,
        string tenantId,
        string serviceId,
        string deploymentId,
        CancellationToken ct)
    {
        using var request = CreateAuthenticatedRequest(
            HttpMethod.Get,
            $"/api/v1/web-services/{serviceId}",
            user,
            tenantId);
        using var response = await Api.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
        var service = document.RootElement;
        if (service.GetProperty("id").GetString() != serviceId)
            throw new InvalidOperationException("The requested workload identity changed.");
        var placement = service.GetProperty("deployments")
            .EnumerateArray()
            .Single(item => item.GetProperty("deploymentId").GetString() == deploymentId);
        if (placement.GetProperty("desiredOperation").GetString() != "Apply")
            throw new InvalidOperationException("The test workload is no longer applied.");
        var groupId = service.GetProperty("resourceGroupId").GetString()!;
        var recorded = _kubernetesNamespaces.TryGetValue(groupId, out var owned)
            ? owned
            : throw new InvalidOperationException(
                "No fixture-owned namespace was recorded for the requested workload.");
        var nativeNamespace = await _kubernetes.CoreV1.ReadNamespaceAsync(recorded.Name, cancellationToken: ct);
        VerifyOwnedNamespace(nativeNamespace, recorded.Uid);
        // Namespace discovery remains an authenticated API read, independently of native labels.
        using var cellsRequest = CreateAuthenticatedRequest(
            HttpMethod.Get,
            $"/api/v1/resource-groups/{groupId}/runtime-cells",
            user,
            tenantId);
        using var cellsResponse = await Api.SendAsync(cellsRequest, ct);
        cellsResponse.EnsureSuccessStatusCode();
        using var cellsDocument = JsonDocument.Parse(await cellsResponse.Content.ReadAsStreamAsync(ct));
        var runtimeCellId = placement.GetProperty("runtimeCellId").GetString()!;
        var cell = cellsDocument.RootElement
            .EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == runtimeCellId);
        if (cell.GetProperty("kubernetesNamespace").GetString() != recorded.Name ||
            cell.GetProperty("deploymentTargetId").GetString() !=
            placement.GetProperty("deploymentTargetId").GetString())
            throw new InvalidOperationException("The current runtime placement does not match the owned namespace.");
        var identity = new WorkloadIdentityLabels(
            tenantId,
            groupId,
            placement.GetProperty("isolationScopeId").GetString()!,
            runtimeCellId,
            placement.GetProperty("deploymentTargetId").GetString()!,
            "WebService",
            serviceId,
            deploymentId);
        return new ObservationPodContext(
            recorded.Name,
            recorded.Uid,
            service.GetProperty("name").GetString()!,
            identity);
    }

    private async Task<IReadOnlyList<OwnedObservationPod>> OwnedObservationPodsAsync(
        ObservationPodContext context,
        CancellationToken ct)
    {
        VerifyOwnedNamespace(
            await _kubernetes.CoreV1.ReadNamespaceAsync(context.Namespace, cancellationToken: ct),
            context.NamespaceUid);
        var controller = await _kubernetes.AppsV1.ReadNamespacedDeploymentAsync(
            context.Name,
            context.Namespace,
            cancellationToken: ct);
        if (!Owns(controller.Metadata))
            throw new InvalidOperationException("The native controller no longer matches the test identity.");
        var selector = string.Join(',', context.Identity.ToDictionary().Select(pair => $"{pair.Key}={pair.Value}"));
        var replicas = await _kubernetes.AppsV1.ListNamespacedReplicaSetAsync(
            context.Namespace,
            labelSelector: selector,
            limit: 100,
            cancellationToken: ct);
        if (!string.IsNullOrEmpty(replicas.Metadata.ContinueProperty))
            throw new InvalidOperationException("The test workload replica-set discovery exceeded its bound.");
        var replicaUids = replicas.Items
            .Where(replica => Owns(replica.Metadata) && replica.Metadata.OwnerReferences?.Any(owner =>
                owner.Controller == true && owner.Kind == "Deployment" && owner.Uid == controller.Metadata.Uid) == true)
            .Select(replica => replica.Metadata.Uid)
            .ToHashSet(StringComparer.Ordinal);
        var pods = await _kubernetes.CoreV1.ListNamespacedPodAsync(
            context.Namespace,
            labelSelector: selector,
            limit: 100,
            cancellationToken: ct);
        if (!string.IsNullOrEmpty(pods.Metadata.ContinueProperty))
            throw new InvalidOperationException("The test workload pod discovery exceeded its bound.");
        var result = pods.Items
            .Where(pod => Owns(pod.Metadata) && pod.Metadata.OwnerReferences?.Any(owner =>
                owner.Controller == true && owner.Kind == "ReplicaSet" && replicaUids.Contains(owner.Uid)) == true)
            .Select(pod => new OwnedObservationPod(
                pod.Metadata.Uid,
                pod.Metadata.Name,
                pod.Metadata.DeletionTimestamp is null &&
                pod.Status?.Conditions?.Any(condition => condition.Type == "Ready" && condition.Status == "True") ==
                true))
            .OrderBy(pod => pod.Name, StringComparer.Ordinal)
            .ToArray();
        if (!await KubernetesWorkloadObservation.MatchesDeploymentBindingAsync(
                _kubernetes,
                context.Name,
                context.Namespace,
                context.Identity,
                controller.Metadata.Uid,
                ct))
            throw new InvalidOperationException("The native test controller changed during pod discovery.");
        return result;

        bool Owns(V1ObjectMeta metadata) => metadata.NamespaceProperty == context.Namespace &&
            KubernetesWorkloadObservation.Owns(metadata, context.Identity);
    }

    public async Task WaitForWebServiceReadyAsync(
        string groupId,
        string name,
        CancellationToken ct)
    {
        await TestWait.UntilAsync(
            async phase =>
            {
                try
                {
                    var deployment = await _kubernetes.AppsV1.ReadNamespacedDeploymentAsync(
                        name,
                        GetNamespaceName(groupId),
                        cancellationToken: phase);
                    if (deployment.Status?.ReadyReplicas >= deployment.Spec.Replicas &&
                        deployment.Status.AvailableReplicas >= deployment.Spec.Replicas &&
                        deployment.Status.UpdatedReplicas >= deployment.Spec.Replicas &&
                        deployment.Status.Replicas == deployment.Spec.Replicas &&
                        deployment.Status.ObservedGeneration >= deployment.Metadata.Generation)
                        return true;
                }
                catch (HttpOperationException exception) when
                    (exception.Response?.StatusCode == HttpStatusCode.NotFound)
                {
                }

                return false;
            },
            TimeSpan.FromMinutes(3),
            "The application workload did not finish rollout within three minutes.",
            ct,
            TimeSpan.FromSeconds(2));
    }


    /// <summary>
    ///     Only forwards application HTTP. Database operations still originate inside the application and traverse its
    ///     CNI policy.
    /// </summary>
    public async Task<(string Postgres, string Redis)> ProbeApplicationDependenciesAsync(
        string groupId,
        string name,
        CancellationToken ct)
    {
        await using var forward = await KubernetesPortForwardSession.StartAsync(
            _kubernetesTarget,
            GetNamespaceName(groupId),
            $"deployment/{name}",
            8080,
            ct);
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        using var response = await client.GetAsync($"http://127.0.0.1:{forward.LocalPort}/dependencies", ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(ct);

        static string Outcome(JsonElement result, string field)
        {
            var value = result.GetProperty(field).GetString();
            return value is "Succeeded" or "ConnectionRejected" or "MissingConfiguration" or "UnexpectedResult"
                ? value
                : throw new InvalidOperationException("The application probe returned an unsupported outcome.");
        }

        return (Outcome(result, "postgres"), Outcome(result, "redis"));
    }

    public async Task<string> GetWebServiceDeploymentDiagnosticsAsync(CancellationToken cancellationToken)
    {
        if (_application is null)
            return "";

        var resourceLogger = _application.Services.GetRequiredService<ResourceLoggerService>();
        var diagnostics = new StringBuilder();
        using var diagnosticTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        diagnosticTimeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await foreach (var batch in resourceLogger.GetAllAsync("pole-server")
                               .WithCancellation(diagnosticTimeout.Token))
            foreach (var line in batch)
            {
                if (!line.Content.Contains("[web-service-deployment-failed]", StringComparison.Ordinal))
                    continue;

                diagnostics.AppendLine(SanitizeDiagnosticLine(line.Content));
                if (diagnostics.Length >= 4_000)
                    return diagnostics.ToString();
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
            return "";

        var resourceLogger = _application.Services.GetRequiredService<ResourceLoggerService>();
        var diagnostics = new StringBuilder();
        using var diagnosticTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        diagnosticTimeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await foreach (var batch in resourceLogger.GetAllAsync("pole-server")
                               .WithCancellation(diagnosticTimeout.Token))
            foreach (var line in batch)
            {
                if (!line.IsErrorMessage && !line.Content.Contains("exception", StringComparison.OrdinalIgnoreCase) &&
                    !line.Content.Contains("fail", StringComparison.OrdinalIgnoreCase))
                    continue;

                diagnostics.AppendLine(SanitizeDiagnosticLine(line.Content));
                if (diagnostics.Length >= 4_000)
                    return diagnostics.ToString();
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Resource logs are a live stream. Diagnostics must not hide the original test failure.
        }

        return diagnostics.ToString();
    }

    public async Task<string> GetDatabaseFailureKindsAsync(CancellationToken cancellationToken)
    {
        // Report exception types/frames and known code messages only. Never expose log arguments,
        // Kubernetes Secret request bodies, connection strings or arbitrary exception messages.
        var resourceLogger = _application!.Services.GetRequiredService<ResourceLoggerService>();
        var kinds = new HashSet<string>(StringComparer.Ordinal);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            await foreach (var batch in resourceLogger.GetAllAsync("pole-server").WithCancellation(timeout.Token))
            foreach (var line in batch)
            {
                foreach (Match match in Regex.Matches(line.Content, @"\b[A-Z][A-Za-z0-9.]*Exception\b"))
                    kinds.Add(match.Value);
                var frame = line.Content.TrimStart();
                if (frame.StartsWith("at Pole.Features.Postgres.", StringComparison.Ordinal) ||
                    frame.StartsWith("at Pole.Features.RedisServices.", StringComparison.Ordinal) || frame.StartsWith(
                        "at Pole.Features.ResourceDeployments.",
                        StringComparison.Ordinal))
                    kinds.Add(frame.Split(" in ")[0]);
                foreach (var message in new[]
                         {
                             "No ingress policy contribution", "Ingress policy revision or TCP port is invalid",
                             "Ingress policy ports do not match the provider path",
                             "Ingress pod source must have an exact DNS namespace and labels", "No service for type",
                             "Unable to resolve service for type",
                         })
                    if (line.Content.Contains(message, StringComparison.Ordinal))
                        kinds.Add(message);
                if (kinds.Count >= 30)
                    return string.Join(Environment.NewLine, kinds.Order());
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }

        return string.Join(Environment.NewLine, kinds.Order());
    }

    public async Task<string> GetGitHubStubRequestDiagnosticsAsync(CancellationToken cancellationToken)
    {
        using var response = await Stub.GetAsync("/__e2e/requests", cancellationToken);
        if (!response.IsSuccessStatusCode)
            return "";

        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        return string.Join(
            Environment.NewLine,
            document.RootElement
                .EnumerateArray()
                .TakeLast(20)
                .Select(request =>
                    $"{request.GetProperty("method").GetString()} {PathWithoutQuery(request.GetProperty("path").GetString())}"));
    }

    private string GetNamespaceName(string resourceGroupId) =>
        _kubernetesNamespaces.TryGetValue(resourceGroupId, out var recorded)
            ? recorded.Name
            : throw new InvalidOperationException(
                $"No runtime-cell namespace was recorded for resource group '{resourceGroupId}'.");

    private async Task<V1Namespace?> ReadNamespaceIfPresentAsync(
        string namespaceName,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _kubernetes.CoreV1.ReadNamespaceAsync(namespaceName, cancellationToken: cancellationToken);
        }
        catch (HttpOperationException exception) when (exception.Response?.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private void VerifyOwnedNamespace(V1Namespace namespaceResource, string? expectedUid = null)
    {
        var labels = namespaceResource.Metadata.Labels;
        if ((expectedUid is not null && !string.Equals(
                namespaceResource.Metadata.Uid,
                expectedUid,
                StringComparison.Ordinal)) || labels is null ||
            !labels.TryGetValue(TestRunIdentity.OwnerLabel, out var owner) ||
            !string.Equals(owner, TestRunIdentity.Owner, StringComparison.Ordinal) ||
            !labels.TryGetValue(TestRunIdentity.WorktreeLabel, out var worktreeId) ||
            !string.Equals(worktreeId, Identity.WorktreeId, StringComparison.Ordinal) ||
            !labels.TryGetValue(TestRunIdentity.RunLabel, out var runId) || !string.Equals(
                runId,
                Identity.RunId,
                StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Kubernetes namespace '{namespaceResource.Metadata.Name}' does not match the E2E test ownership contract.");
    }

    private async Task StartLabServiceAccessAsync(CancellationToken cancellationToken)
    {
        using var timing = _timings.Measure("fixture.e2e.lab-service-access");
        if (!string.Equals(_lab.State.Profile, "full", StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"The Aspire E2E suite requires Kubernetes lab profile 'full', but '{_lab.State.Profile}' is active.");

        _harborCredentials = await ReadHarborCredentialsAsync(_labServices.Harbor.CredentialsPath, cancellationToken);
        using (_timings.Measure("fixture.e2e.harbor.forward.start"))
        {
            _harborPortForward = await KubernetesPortForwardSession.StartAsync(
                _serviceTarget,
                _labServices.Harbor.Namespace,
                $"service/{_labServices.Harbor.Service}",
                _labServices.Harbor.Port,
                cancellationToken);
        }

        try
        {
            using (_timings.Measure("fixture.e2e.buildkit.forward.start"))
            {
                _buildKitPortForward = await KubernetesPortForwardSession.StartAsync(
                    _serviceTarget,
                    _labServices.BuildKit.Namespace,
                    $"service/{_labServices.BuildKit.Service}",
                    _labServices.BuildKit.Port,
                    cancellationToken);
            }
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

        foreach (var (key, value) in Identity.NamespaceLabels)
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
                _kubernetesTarget.PublicBaseDomain)
            .WithEnvironment(
                $"DeploymentTargets__Bootstrap__WorkloadConfiguration__NetworkSources__Pods__{DeploymentTargetNetworkSourcesDto.PublicIngressName}__Namespace",
                "kube-system")
            .WithEnvironment(
                $"DeploymentTargets__Bootstrap__WorkloadConfiguration__NetworkSources__Pods__{DeploymentTargetNetworkSourcesDto.PublicIngressName}__PodLabels__app.kubernetes.io/name",
                "traefik")
            .WithEnvironment(
                $"DeploymentTargets__Bootstrap__WorkloadConfiguration__NetworkSources__Pods__{DeploymentTargetNetworkSourcesDto.PublicIngressName}__PodLabels__app.kubernetes.io/instance",
                "traefik-kube-system")
            .WithEnvironment("DeploymentTargets__Bootstrap__WorkloadConfiguration__NetworkSources__Pods__postgres/operator__Namespace",
                "pole-lab-cnpg")
            .WithEnvironment("DeploymentTargets__Bootstrap__WorkloadConfiguration__NetworkSources__Pods__postgres/operator__PodLabels__app.kubernetes.io/instance",
                "pole-lab-cnpg");
    }

    internal static async Task WaitForStartupAsync(
        Task starting,
        Task apiReady,
        Task mongoExited)
    {
        // Running is not Mongo readiness. Keep observing its process while replica-set/API startup is pending.
        var pending = new List<Task> { starting, apiReady };
        while (pending.Count > 0)
        {
            var completed = await Task.WhenAny(pending.Append(mongoExited));
            await completed;
            if (completed == mongoExited)
                throw new InvalidOperationException("MongoDB exited before the API was ready.");
            pending.Remove(completed);
        }
    }

    private async Task<string> GetMongoStartupErrorsAsync()
    {
        var errors = new List<MongoStartupError>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            var logger = _application!.Services.GetRequiredService<ResourceLoggerService>();
            var inspected = 0;
            await foreach (var batch in logger.GetAllAsync("pole-mongo").WithCancellation(timeout.Token))
            foreach (var line in batch)
            {
                if (MongoStartupError.Parse(line.Content) is { } error)
                    errors.Add(error);
                if (++inspected >= 2_000 || errors.Count >= 8)
                    return string.Join("; ", errors);
            }
        }
        catch (Exception)
        {
            // Diagnostic failure must neither hide the startup error nor expose exception payloads.
        }

        return errors.Count == 0 ? "MongoDB native error details unavailable." : string.Join("; ", errors);
    }

    private string DescribeStartupStates(IEnumerable<string> resourceNames)
    {
        // Never serialize resource snapshots: they can contain credentials, URLs and environment values.
        try
        {
            return string.Join(
                "; ",
                resourceNames.Select(name =>
                {
                    if (!_application!.ResourceNotifications.TryGetCurrentState(name, out var resource))
                        return $"{name}: no snapshot";
                    var snapshot = resource.Snapshot;
                    var observedState = snapshot.State?.Text;
                    var state = observedState switch
                    {
                        "Starting" or "Running" or "FailedToStart" or "RuntimeUnhealthy" or "Waiting" or "NotStarted"
                            or "Building" or "Stopping" or "Exited" or "Finished" or "Terminated" or "Hidden"
                            or "ValueMissing" or "Active" => observedState,
                        null => "none",
                        _ => "Other",
                    };
                    return
                        $"{name}: state={state}, health={snapshot.HealthStatus?.ToString() ?? "none"}, exit={snapshot.ExitCode?.ToString() ?? "none"}";
                }));
        }
        catch
        {
            // Diagnostic collection must not replace the original startup failure or expose exception payloads.
            return "Resource states unavailable.";
        }
    }

    private async Task WaitForRequiredResourceHealthyAsync(string resourceName, CancellationToken cancellationToken)
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

        await portForward.DisposeAsync();
    }

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
            "Realtime:Enabled=true",
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
            $"E2E:WorkspaceRoot={Path.Combine(scratchDirectory, "source-workspace")}",
        };

        return arguments.ToArray();
    }

    private static string SanitizeDiagnosticLine(string line) => Regex.Replace(
        Regex.Replace(
            Regex.Replace(line, "(?i)(authorization)\\s*[:=]\\s*(?:Basic|Bearer)\\s+\\S+", "$1=[redacted]"),
            "(?i)Bearer\\s+\\S+",
            "Bearer [redacted]"),
        "(?i)(password|token|secret)(?:[\\\"']\\s*)?[:=]\\s*(?:[\\\"'])?[^\\s,}\\]\\\"']+",
        "$1=[redacted]");

    private static string PathWithoutQuery(string? path) => path?.Split('?', 2)[0] ?? "";

    internal static TcpListener ReserveTcpPort()
    {
        // Kestrel's localhost endpoints bind both address families. An IPv4-only
        // reservation leaves the same IPv6 port available to another Aspire resource.
        var listener = new TcpListener(Socket.OSSupportsIPv6 ? IPAddress.IPv6Any : IPAddress.Loopback, 0);
        if (Socket.OSSupportsIPv6)
            listener.Server.DualMode = true;
        listener.ExclusiveAddressUse = true;
        listener.Start();
        return listener;
    }

    private async Task ConfigureInteractiveClientAsync(CancellationToken cancellationToken)
    {
        using var timing = _timings.Measure("fixture.e2e.keycloak.ui-client.configure");
        var client = await _keycloak!.GetClientAsync("pole.control-plane.ui", cancellationToken);
        var originalUiClient = client.DeepClone().AsObject();
        var uiClientInternalId = client["id"]?.GetValue<string>() ??
            throw new InvalidOperationException("Keycloak did not return the UI client id.");
        var frontendOrigin = FrontendBaseAddress.GetLeftPart(UriPartial.Authority);
        client["redirectUris"] = new JsonArray($"{frontendOrigin}/*");
        client["webOrigins"] = new JsonArray(frontendOrigin);
        var attributes = client["attributes"] as JsonObject ?? new JsonObject();
        client["attributes"] = attributes;
        attributes["post.logout.redirect.uris"] = "+";
        await _keycloak.UpdateClientAsync(uiClientInternalId, client, cancellationToken);
        _cleanup.Add(
            ("fixture.e2e.cleanup.keycloak.ui-client.restore",
                ct => _keycloak.UpdateClientAsync(uiClientInternalId, originalUiClient, ct)));
    }

    public async Task UseInteractiveClientTokenLifespanAsync(
        TestResourceScope resources,
        TimeSpan lifespan,
        CancellationToken cancellationToken)
    {
        var seconds = checked((int)lifespan.TotalSeconds);
        if (seconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(lifespan), "The UI token lifespan must be positive.");
        var keycloak = _keycloak ??
            throw new InvalidOperationException("The E2E identity provider is not initialized.");
        var client = await keycloak.GetClientAsync("pole.control-plane.ui", cancellationToken);
        var internalId = client["id"]?.GetValue<string>() ??
            throw new InvalidOperationException("Keycloak did not return the UI client id.");
        var original = client.DeepClone().AsObject();
        // Register restoration before updating. This is the UI client only; the realm and the
        // separate direct-grant client used by fixture API calls retain their normal lifespans.
        resources.Defer(
            "restore interactive client token lifespan",
            token => keycloak.UpdateClientAsync(internalId, original, token));
        var attributes = client["attributes"] as JsonObject ?? new JsonObject();
        client["attributes"] = attributes;
        attributes["access.token.lifespan"] = seconds.ToString(CultureInfo.InvariantCulture);
        await keycloak.UpdateClientAsync(internalId, client, cancellationToken);
    }

    private async Task<TestUser> CreateBootstrapUserAsync(CancellationToken cancellationToken)
    {
        using var timing = _timings.Measure("fixture.e2e.keycloak.bootstrap-user.create");
        var password = $"Bootstrap-{Guid.NewGuid():N}-1!";
        var email = $"bootstrap-{Guid.NewGuid():N}@e2e.pole.test";
        var keycloakUserId = await _keycloak!.CreateUserAsync(email, "E2E", "Bootstrap", password, cancellationToken);
        _cleanup.Add(("fixture.e2e.cleanup.keycloak.user.delete", ct => _keycloak.DeleteUserAsync(keycloakUserId, ct)));
        return new TestUser(email, password, keycloakUserId, "", "");
    }

    private async Task<TestUser> CreatePlatformUserAsync(
        string prefix,
        string bootstrapToken,
        DirectGrantClient directGrantClient,
        CancellationToken cancellationToken)
    {
        using var timing = _timings.Measure("fixture.e2e.platform-user.create");
        var email = $"{prefix}-{Guid.NewGuid():N}@e2e.pole.test";
        using var create = new HttpRequestMessage(HttpMethod.Post, "/api/v1/users")
        {
            Content = JsonContent.Create(new { email, firstName = "E2E", lastName = prefix }),
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
        var keycloakUserId = document.RootElement.GetProperty("id").GetString() ??
            throw new InvalidOperationException("Pole did not return the created user id.");

        var password = $"{prefix}-{Guid.NewGuid():N}-1!";
        await _keycloak!.SetPasswordAsync(keycloakUserId, password, cancellationToken);
        await _keycloak.MakeUserInteractiveAsync(keycloakUserId, cancellationToken);
        _cleanup.Add(("fixture.e2e.cleanup.keycloak.user.delete", ct => _keycloak.DeleteUserAsync(keycloakUserId, ct)));

        var accessToken = await _keycloak.GetPasswordGrantTokenAsync(
            directGrantClient.ClientId,
            directGrantClient.Secret,
            email,
            password,
            cancellationToken);
        RegisterApiToken(accessToken, email, password, directGrantClient);
        var tenantId = await WaitForDefaultTenantAsync(accessToken, cancellationToken);
        return new TestUser(email, password, keycloakUserId, accessToken, tenantId);
    }

    private void RegisterApiToken(
        string token,
        string email,
        string password,
        DirectGrantClient directGrantClient) => Api.RegisterToken(
        token,
        ct => _keycloak!.GetPasswordGrantTokenAsync(
            directGrantClient.ClientId,
            directGrantClient.Secret,
            email,
            password,
            ct));

    private async Task<string> WaitForDefaultTenantAsync(string accessToken, CancellationToken cancellationToken)
    {
        using var timing = _timings.Measure("fixture.e2e.default-tenant.ready");
        string? result = null;
        await TestWait.UntilAsync(
            async phase =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/tenants");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                using var response = await Api.SendAsync(request, phase);
                if (response.IsSuccessStatusCode)
                {
                    using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(phase));
                    var tenant = document.RootElement
                        .EnumerateArray()
                        .FirstOrDefault(item =>
                            item.TryGetProperty("isDefault", out var isDefault) && isDefault.GetBoolean());
                    if (tenant.ValueKind != JsonValueKind.Undefined)
                        result = tenant.GetProperty("id").GetString() ??
                            throw new InvalidOperationException("Default tenant has no id.");
                    if (result is not null)
                        return true;
                }
                else
                {
                    throw new HttpRequestException(
                        $"Default tenant discovery returned HTTP {(int)response.StatusCode}.",
                        null,
                        response.StatusCode);
                }

                return false;
            },
            TimeSpan.FromMinutes(2),
            "The default tenant was not created from the UserCreated outbox event.",
            cancellationToken,
            TimeSpan.FromSeconds(1));
        return result!;
    }


    private async Task WaitForHarborHealthAsync(CancellationToken cancellationToken)
    {
        using var timing = _timings.Measure("fixture.e2e.harbor.ready");
        using var client = new HttpClient(new HttpClientHandler { UseCookies = false })
        {
            BaseAddress = HarborBaseAddress,
        };
        await TestWait.UntilAsync(
            async phase =>
            {
                try
                {
                    using var response = await client.GetAsync("/api/v2.0/health", phase);
                    if (!response.IsSuccessStatusCode)
                        throw new HttpRequestException("Harbor API is not ready.");

                    // The API can become healthy before the registry endpoint is ready to accept
                    // BuildKit pushes. A 401 is the expected unauthenticated response from /v2/.
                    using var registryResponse = await client.GetAsync("/v2/", phase);
                    if (registryResponse.IsSuccessStatusCode ||
                        registryResponse.StatusCode == HttpStatusCode.Unauthorized)
                        return true;
                }
                catch (HttpRequestException)
                {
                    // Harbor's Compose topology is still starting.
                }

                return false;
            },
            TimeSpan.FromMinutes(3),
            "Harbor did not report healthy within three minutes.",
            cancellationToken,
            TimeSpan.FromSeconds(2));
    }


    private async Task WaitForBuildKitReadyAsync(CancellationToken cancellationToken)
    {
        using var timing = _timings.Measure("fixture.e2e.buildkit.ready");
        await TestWait.UntilAsync(
            async phase =>
            {
                if (await IsBuildKitReadyAsync(phase))
                    return true;

                return false;
            },
            TimeSpan.FromMinutes(1),
            "BuildKit did not accept a buildctl connection on its E2E endpoint.",
            cancellationToken,
            TimeSpan.FromSeconds(1));
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
                CreateNoWindow = true,
            },
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

    private static async Task WaitForKeycloakDiscoveryAsync(HttpClient keycloak, CancellationToken cancellationToken)
    {
        await TestWait.UntilAsync(
            async phase =>
            {
                try
                {
                    using var response = await keycloak.GetAsync(
                        "/realms/pole.control-plane/.well-known/openid-configuration",
                        phase);
                    if (response.IsSuccessStatusCode)
                        return true;
                }
                catch (HttpRequestException)
                {
                    // Keycloak is still importing the realm.
                }

                return false;
            },
            TimeSpan.FromMinutes(2),
            "Keycloak did not expose OpenID discovery within two minutes.",
            cancellationToken,
            TimeSpan.FromSeconds(1));
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
                RedirectStandardError = true,
            },
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
                RedirectStandardError = true,
            },
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

    private sealed record ObservationPodContext(
        string Namespace,
        string NamespaceUid,
        string Name,
        WorkloadIdentityLabels Identity
    );
}

public sealed record TestUser(
    string Email,
    string Password,
    string KeycloakUserId,
    string AccessToken,
    string DefaultTenantId
);

internal sealed record HarborCredentials(
    string Username,
    string Password
);

internal sealed record RecordedNamespace(
    string Name,
    string Uid
);

public sealed record OwnedObservationPod(
    string Uid,
    string Name,
    bool Ready
);
