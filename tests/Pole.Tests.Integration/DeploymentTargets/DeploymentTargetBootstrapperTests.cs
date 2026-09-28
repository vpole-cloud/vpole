using System.Collections.Concurrent;
using k8s;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Encryption;
using Pole.Features.DeploymentTargets.Application.Bootstrap;
using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Application.Connections;
using Pole.Features.DeploymentTargets.Application.Endpoints;
using Pole.Features.DeploymentTargets.Application.Persistence;
using Pole.Features.DeploymentTargets.Application.Persistence.Bootstrap;
using Pole.Features.DeploymentTargets.Application.Persistence.Mutations;
using Pole.Features.DeploymentTargets.Application.Persistence.References;
using Pole.Features.DeploymentTargets.Application.Validation;
using Pole.Features.DeploymentTargets.Domain;
using Pole.ResultType;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.DeploymentTargets;

[Collection(IntegrationTestCollection.Name)]
public sealed class DeploymentTargetBootstrapperTests(MongoReplicaSetFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 7, 22, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task ProductionAuto_UsesInClusterConnection()
    {
        var preparer = new RecordingConnectionPreparer();
        var bootstrapper = CreateBootstrapper(preparer, Environments.Production);

        await bootstrapper.InvokeAsync();

        Assert.IsType<InClusterConnectionInput>(Assert.Single(preparer.Inputs));
        var (target, settings) = await LoadSingleStateAsync();
        Assert.IsType<InClusterDeploymentTargetConnection>(target.Connection);
        var ingress = Assert.IsType<DeploymentTargetIngressConfiguration>(
            target.WorkloadConfiguration.Ingress);
        Assert.Equal("traefik", ingress.IngressClassName);
        Assert.Equal("letsencrypt-prod", ingress.ClusterIssuerName);
        Assert.Equal(1, target.WorkloadConfiguration.Revision);
        Assert.Equal(target.Id, settings.DefaultDeploymentTargetId);
    }

    [Fact]
    public async Task DevelopmentAuto_ImportsConfiguredKubeconfigContentAndContext()
    {
        const string kubeconfig = "apiVersion: v1\nkind: Config\ncurrent-context: imported-context\n";
        const string contextName = "explicit-context";
        var kubeconfigPath = Path.Combine(
            Path.GetTempPath(),
            $"pole-bootstrap-{Guid.NewGuid():N}.yaml");
        await File.WriteAllTextAsync(kubeconfigPath, kubeconfig);

        try
        {
            var preparer = new RecordingConnectionPreparer();
            var options = CreateOptions(new DeploymentTargetBootstrapOptions
            {
                Mode = DeploymentTargetBootstrapMode.Auto,
                KubeconfigPath = kubeconfigPath,
                KubeconfigContext = contextName
            });
            var bootstrapper = CreateBootstrapper(
                preparer,
                Environments.Development,
                options);

            await bootstrapper.InvokeAsync();

            var input = Assert.IsType<KubeconfigConnectionInput>(Assert.Single(preparer.Inputs));
            Assert.Equal(kubeconfig, input.Kubeconfig);
            Assert.Equal(contextName, input.ContextName);
            var (target, settings) = await LoadSingleStateAsync();
            Assert.IsType<KubeconfigDeploymentTargetConnection>(target.Connection);
            Assert.Equal(target.Id, settings.DefaultDeploymentTargetId);
        }
        finally
        {
            File.Delete(kubeconfigPath);
        }
    }

    [Fact]
    public async Task RepeatedBootstrap_IsIdempotent()
    {
        var preparer = new RecordingConnectionPreparer();
        var probe = new SuccessfulProbe();
        var bootstrapper = CreateBootstrapper(
            preparer,
            Environments.Production,
            probe: probe);

        await bootstrapper.InvokeAsync();
        await bootstrapper.InvokeAsync();

        Assert.Single(preparer.Inputs);
        Assert.Equal(1, probe.CallCount);
        var (target, settings) = await LoadSingleStateAsync();
        Assert.Equal(target.Id, settings.DefaultDeploymentTargetId);
    }

    [Fact]
    public async Task ConcurrentBootstrap_CreatesExactlyOneTargetAndDefaultSetting()
    {
        var gate = new AsyncArrivalGate(participantCount: 2);
        var firstPreparer = new RecordingConnectionPreparer(gate.ArriveAndWaitAsync);
        var secondPreparer = new RecordingConnectionPreparer(gate.ArriveAndWaitAsync);
        var firstBootstrapper = CreateBootstrapper(firstPreparer, Environments.Production);
        var secondBootstrapper = CreateBootstrapper(secondPreparer, Environments.Production);

        await Task.WhenAll(
            firstBootstrapper.InvokeAsync(),
            secondBootstrapper.InvokeAsync());

        Assert.Single(firstPreparer.Inputs);
        Assert.Single(secondPreparer.Inputs);
        var (target, settings) = await LoadSingleStateAsync();
        Assert.Equal(target.Id, settings.DefaultDeploymentTargetId);
    }

    [Fact]
    public async Task ExistingTargetWithoutSettings_CreatesDefaultSetting()
    {
        var existing = CreateExistingTarget();
        await fixture.DbContext.DeploymentTargets.InsertOneAsync(existing);
        var preparer = new RecordingConnectionPreparer();
        var probe = new SuccessfulProbe();
        var bootstrapper = CreateBootstrapper(
            preparer,
            Environments.Production,
            probe: probe);

        await bootstrapper.InvokeAsync();

        Assert.Empty(preparer.Inputs);
        Assert.Equal(0, probe.CallCount);
        var (target, settings) = await LoadSingleStateAsync();
        Assert.Equal(existing.Id, target.Id);
        Assert.Equal(existing.Id, settings.DefaultDeploymentTargetId);
    }

    [Fact]
    public async Task DanglingDefaultSetting_FailsWithoutCreatingTarget()
    {
        var missingTargetId = ObjectId.GenerateNewId().ToString();
        await fixture.DbContext.DeploymentTargetSettings.InsertOneAsync(
            DeploymentTargetSettings.Create(missingTargetId, Now.UtcDateTime));
        var preparer = new RecordingConnectionPreparer();
        var bootstrapper = CreateBootstrapper(preparer, Environments.Production);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => bootstrapper.InvokeAsync());

        Assert.Equal(
            "Deployment-target settings reference a target that does not exist.",
            exception.Message);
        Assert.Empty(preparer.Inputs);
        Assert.Equal(
            0,
            await fixture.DbContext.DeploymentTargets.CountDocumentsAsync(
                Builders<DeploymentTarget>.Filter.Empty));
        Assert.Equal(
            1,
            await fixture.DbContext.DeploymentTargetSettings.CountDocumentsAsync(
                Builders<DeploymentTargetSettings>.Filter.Empty));
    }

    [Fact]
    public async Task DevelopmentAuto_RejectsRelativeKubeconfigEnvironmentPath()
    {
        var previous = Environment.GetEnvironmentVariable("KUBECONFIG");
        Environment.SetEnvironmentVariable("KUBECONFIG", "relative-kubeconfig.yaml");
        try
        {
            var preparer = new RecordingConnectionPreparer();
            var bootstrapper = CreateBootstrapper(preparer, Environments.Development);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => bootstrapper.InvokeAsync());

            Assert.Equal(
                "Development bootstrap requires an absolute KUBECONFIG file path.",
                exception.Message);
            Assert.Empty(preparer.Inputs);
        }
        finally
        {
            Environment.SetEnvironmentVariable("KUBECONFIG", previous);
        }
    }

    [Fact]
    public void OptionsValidator_RejectsUndefinedBootstrapMode()
    {
        var options = CreateOptions(new DeploymentTargetBootstrapOptions
        {
            Mode = (DeploymentTargetBootstrapMode)999
        });

        var result = new DeploymentTargetOptionsValidator().Validate(Options.DefaultName, options);

        Assert.True(result.Failed);
        Assert.Contains(
            "DeploymentTargets:Bootstrap:Mode is not supported.",
            result.Failures);
    }

    [Fact]
    public async Task Bootstrap_PersistsConfiguredWorkloadConfiguration()
    {
        var options = CreateOptions(new DeploymentTargetBootstrapOptions
        {
            WorkloadConfiguration = new DeploymentTargetBootstrapWorkloadConfigurationOptions
            {
                Ingress = new DeploymentTargetBootstrapIngressOptions
                {
                    IngressClassName = "nginx",
                    ClusterIssuerName = "private-ca"
                },
                NetworkSources = new DeploymentTargetBootstrapNetworkSourcesOptions
                {
                    Pods = new()
                    {
                        ["public-ingress"] = new()
                        {
                            Namespace = "ingress-system",
                            PodLabels = new() { ["app"] = "nginx" }
                        }
                    },
                    Cidrs = new()
                    {
                        ["functions/gateway"] = "192.0.2.9/32"
                    }
                }
            }
        });
        var bootstrapper = CreateBootstrapper(
            new RecordingConnectionPreparer(),
            Environments.Production,
            options);

        await bootstrapper.InvokeAsync();

        var (target, _) = await LoadSingleStateAsync();
        var ingress = Assert.IsType<DeploymentTargetIngressConfiguration>(
            target.WorkloadConfiguration.Ingress);
        Assert.Equal("nginx", ingress.IngressClassName);
        Assert.Equal("private-ca", ingress.ClusterIssuerName);
        var ingressSource = Assert.IsType<DeploymentTargetPodNetworkSource>(
            target.WorkloadConfiguration.NetworkSources.Get("public-ingress"));
        Assert.Equal("ingress-system", ingressSource.Namespace);
        Assert.Equal("nginx", ingressSource.PodLabels["app"]);
        Assert.Equal("192.0.2.9/32", Assert.IsType<DeploymentTargetCidrNetworkSource>(
            target.WorkloadConfiguration.NetworkSources.Get("functions/gateway")).Cidr);
        Assert.Null(target.WorkloadConfiguration.NetworkSources.Get("postgres/operator"));
    }

    [Fact]
    public void OptionsValidator_RejectsInvalidIngressConfiguration()
    {
        var options = CreateOptions(new DeploymentTargetBootstrapOptions
        {
            WorkloadConfiguration = new DeploymentTargetBootstrapWorkloadConfigurationOptions
            {
                Ingress = new DeploymentTargetBootstrapIngressOptions
                {
                    IngressClassName = "Invalid_Ingress"
                }
            }
        });

        var result = new DeploymentTargetOptionsValidator().Validate(Options.DefaultName, options);

        Assert.True(result.Failed);
        Assert.Contains(
            "DeploymentTargets:Bootstrap:WorkloadConfiguration:Ingress:IngressClassName " +
            "must be a valid Kubernetes resource name.",
            result.Failures);
    }

    [Fact]
    public async Task DefaultBindingRacingDeletion_CannotLeaveDanglingSettings()
    {
        var existing = CreateExistingTarget();
        await fixture.DbContext.DeploymentTargets.InsertOneAsync(existing);
        var deleteClock = new BlockingTimeProvider(Now.AddMinutes(1));
        var deleteHandler = new DeleteDeploymentTargetHandler(
            new MongoDeleteDeploymentTarget(
                fixture.DbContext.DeploymentTargets,
                fixture.DbContext.DeploymentTargetSettings,
                new MongoEnsureNoDeploymentTargetReferences(
                        fixture.DbContext.DeploymentTargetReferences)
                    .InvokeAsync,
                new MongoRunTransaction(fixture.Client)
                    .InvokeOutcomeAsync<DeleteDeploymentTargetPersistenceOutcome>,
                deleteClock).InvokeAsync,
            new NoOpClientCache().Invalidate);

        var deletion = Task.Run(
            () => deleteHandler.HandleAsync(new DeleteDeploymentTargetRequest(existing.Id)));
        var bootstrapper = CreateBootstrapper(
            new RecordingConnectionPreparer(),
            Environments.Production);
        try
        {
            await deleteClock.WaitUntilReadAsync().WaitAsync(TestTimeout);
            await bootstrapper.InvokeAsync().WaitAsync(TestTimeout);
        }
        finally
        {
            deleteClock.Release();
        }

        var deletionResult = await deletion.WaitAsync(TestTimeout);
        Assert.True(deletionResult is DeleteDeploymentTargetError);
        var (target, settings) = await LoadSingleStateAsync();
        Assert.Equal(existing.Id, target.Id);
        Assert.Null(target.DeletionRequestedAtUtc);
        Assert.True(target.AcceptsWorkloads);
        Assert.Equal(existing.Revision + 1, target.Revision);
        Assert.Equal(target.Id, settings.DefaultDeploymentTargetId);
    }

    private DeploymentTargetBootstrapper CreateBootstrapper(
        RecordingConnectionPreparer preparer,
        string environmentName,
        DeploymentTargetOptions? options = null,
        SuccessfulProbe? probe = null)
    {
        var timeProvider = new FixedTimeProvider(Now);
        EnsureNoOverlappingDeploymentTargetPublicBaseDomain ensureNoOverlappingPublicBaseDomain =
            new MongoEnsureNoOverlappingDeploymentTargetPublicBaseDomain(
            fixture.DbContext.DeploymentTargets,
            fixture.Database.GetCollection<DeploymentTargetPublicBaseDomainAdmissionFence>(
                DeploymentTargetPublicBaseDomainAdmissionFence.CollectionName)).InvokeAsync;
        return new DeploymentTargetBootstrapper(
            new MongoLoadDeploymentTargetBootstrapState(
                fixture.DbContext.DeploymentTargets,
                fixture.DbContext.DeploymentTargetSettings).InvokeAsync,
            new MongoEnsureBootstrapDefaultDeploymentTarget(
                fixture.DbContext.DeploymentTargets,
                fixture.DbContext.DeploymentTargetSettings,
                ensureNoOverlappingPublicBaseDomain,
                new MongoRunTransaction(fixture.Client)
                    .InvokeOutcomeAsync<EnsureBootstrapDefaultDeploymentTargetOutcome>,
                timeProvider).InvokeAsync,
            preparer.InvokeAsync,
            (probe ?? new SuccessfulProbe()).InvokeAsync,
            options ?? CreateOptions(new DeploymentTargetBootstrapOptions()),
            new FakeHostEnvironment(environmentName),
            timeProvider,
            NullLogger<DeploymentTargetBootstrapper>.Instance);
    }

    private async Task<(DeploymentTarget Target, DeploymentTargetSettings Settings)> LoadSingleStateAsync()
    {
        var targets = await fixture.DbContext.DeploymentTargets
            .Find(Builders<DeploymentTarget>.Filter.Empty)
            .ToListAsync();
        var settings = await fixture.DbContext.DeploymentTargetSettings
            .Find(Builders<DeploymentTargetSettings>.Filter.Empty)
            .ToListAsync();
        return (Assert.Single(targets), Assert.Single(settings));
    }

    private static DeploymentTargetOptions CreateOptions(DeploymentTargetBootstrapOptions bootstrap) =>
        new()
        {
            Bootstrap = bootstrap
        };

    private static DeploymentTarget CreateExistingTarget()
    {
        var id = ObjectId.GenerateNewId().ToString();
        const long connectionVersion = 1;
        return DeploymentTargetFactory.Create(
            "Existing cluster",
            $"kubernetes:kube-system:{id}",
            DeploymentTargetConnectionFactory.CreateInCluster(connectionVersion),
            DeploymentTargetHealth.Healthy(connectionVersion, Now.UtcDateTime),
            DeploymentTargetCapabilities.Unknown(connectionVersion),
            DeploymentTargetTestData.CreateWorkloadConfiguration(),
            enabled: true,
            acceptsWorkloads: true,
            new DeploymentTargetFactory.CreateOptions(id, Now.UtcDateTime));
    }

    private static IKubernetes CreateInertKubernetesClient() =>
        new Kubernetes(
            new KubernetesClientConfiguration
            {
                Host = "https://bootstrap.invalid"
            },
            new InertHttpMessageHandler());

    private static EncryptedValue CreateFakeEnvelope() =>
        new(
            FormatVersion: 1,
            KeyProviderId: "fake-provider",
            KeyEncryptionKeyId: "fake-key",
            KeyEncryptionKeyVersion: 1,
            KeyWrapAlgorithm: "fake-wrap",
            KeyWrapAlgorithmVersion: 1,
            WrappedDataEncryptionKey: [1],
            WrappedDataEncryptionKeyNonce: [2],
            WrappedDataEncryptionKeyTag: [3],
            ContentEncryptionAlgorithm: "fake-content",
            ContentEncryptionAlgorithmVersion: 1,
            Ciphertext: [4],
            Nonce: [5],
            AuthenticationTag: [6]);

    private sealed class RecordingConnectionPreparer(
        Func<Task>? beforeReturn = null)
    {
        private readonly ConcurrentQueue<DeploymentTargetConnectionInput> _inputs = [];

        public IReadOnlyCollection<DeploymentTargetConnectionInput> Inputs => _inputs.ToArray();

        public async ValueTask<PrepareDeploymentTargetConnectionOutcome> InvokeAsync(
            string deploymentTargetId,
            long connectionVersion,
            DeploymentTargetConnectionInput input,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _inputs.Enqueue(input);
            if (beforeReturn is not null)
                await beforeReturn();

            DeploymentTargetConnection connection = input switch
            {
                InClusterConnectionInput => DeploymentTargetConnectionFactory.CreateInCluster(connectionVersion),
                KubeconfigConnectionInput kubeconfig => DeploymentTargetConnectionFactory.CreateKubeconfig(
                    connectionVersion,
                    kubeconfig.ContextName ?? "captured-context",
                    "https://bootstrap.invalid",
                    CreateFakeEnvelope()),
                _ => throw new InvalidOperationException("Unexpected bootstrap connection input.")
            };
            return new PrepareDeploymentTargetConnectionOutcome(
                new PreparedDeploymentTargetConnection(connection, CreateInertKubernetesClient()));
        }
    }

    private sealed class SuccessfulProbe
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public Task<DeploymentTargetProbeObservation> InvokeAsync(
            IKubernetes kubernetes,
            long connectionVersion,
            DateTime? lastHealthyAtUtc = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(kubernetes);
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _callCount);
            return Task.FromResult(
                new DeploymentTargetProbeObservation(
                    "kubernetes:kube-system:bootstrap-test-cluster",
                    DeploymentTargetHealth.Healthy(connectionVersion, Now.UtcDateTime),
                    DeploymentTargetCapabilities.Unknown(connectionVersion)));
        }
    }

    private sealed class FakeHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "Pole.Tests.Integration";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class BlockingTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private readonly TaskCompletionSource _read =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WaitUntilReadAsync() => _read.Task;

        public void Release() => _release.TrySetResult();

        public override DateTimeOffset GetUtcNow()
        {
            _read.TrySetResult();
            if (!_release.Task.Wait(TestTimeout))
                throw new TimeoutException("The deployment-target delete race gate was not released.");
            return utcNow;
        }
    }

    private sealed class NoOpClientCache
    {
        public void Invalidate(string deploymentTargetId)
        {
        }
    }

    private sealed class AsyncArrivalGate(int participantCount)
    {
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _remaining = participantCount;

        public Task ArriveAndWaitAsync()
        {
            if (Interlocked.Decrement(ref _remaining) == 0)
                _release.TrySetResult();
            return _release.Task;
        }
    }

    private sealed class InertHttpMessageHandler : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The bootstrap test Kubernetes client must remain inert.");
    }
}
