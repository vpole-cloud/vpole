using k8s;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.DeploymentTargets.Application.Connections;
using Pole.Features.DeploymentTargets.Application.Persistence;
using Pole.Features.DeploymentTargets.Application.Persistence.Reads;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.DeploymentTargets.Domain;
using Pole.ResultType;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.DeploymentTargets;

[Collection(IntegrationTestCollection.Name)]
public sealed class KubernetesClientProviderTests(MongoReplicaSetFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task GetAsync_RoutesEachTargetToItsOwnClient_AndDefaultUsesConfiguredTarget()
    {
        var firstTarget = CreateTarget();
        var secondTarget = CreateTarget();
        await fixture.DbContext.DeploymentTargets.InsertManyAsync([firstTarget, secondTarget]);
        await fixture.DbContext.DeploymentTargetSettings.InsertOneAsync(
            DeploymentTargetSettings.Create(secondTarget.Id, DateTime.UtcNow));
        var factory = new RecordingClientFactory();
        using var provider = CreateProvider(factory);

        var firstClient = await provider.GetAsync(firstTarget.Id);
        var secondClient = await provider.GetAsync(secondTarget.Id);
        var defaultClient = await provider.GetDefaultAsync();

        Assert.NotSame(firstClient, secondClient);
        Assert.Same(secondClient, defaultClient);
        Assert.Collection(
            factory.Calls,
            call => Assert.Equal(firstTarget.Id, call.DeploymentTargetId),
            call => Assert.Equal(secondTarget.Id, call.DeploymentTargetId));
    }

    [Fact]
    public async Task GetAsync_ReusesClientWhileRevisionAndConnectionVersionAreUnchanged()
    {
        var target = CreateTarget();
        await fixture.DbContext.DeploymentTargets.InsertOneAsync(target);
        var factory = new RecordingClientFactory();
        using var provider = CreateProvider(factory);

        var firstClient = await provider.GetAsync(target.Id);
        var secondClient = await provider.GetAsync(target.Id);

        Assert.Same(firstClient, secondClient);
        var call = Assert.Single(factory.Calls);
        Assert.Equal(target.Connection.Version, call.ConnectionVersion);
    }

    [Fact]
    public async Task GetAsync_ReusesClientAfterMetadataRevision_AndRebuildsAfterConnectionRotation()
    {
        var target = CreateTarget();
        await fixture.DbContext.DeploymentTargets.InsertOneAsync(target);
        var factory = new RecordingClientFactory();
        using var provider = CreateProvider(factory);

        var initialClient = await provider.GetAsync(target.Id);

        var metadataRevision = target.UpdateMetadata(
            $"{target.DisplayName} updated",
            target.UpdatedAtUtc.AddSeconds(1));
        await ReplaceTargetAsync(metadataRevision);
        var clientAfterRevision = await provider.GetAsync(target.Id);

        var connectionRevision = metadataRevision.ReplaceConnectionCredential(
            metadataRevision.ClusterIdentity,
            DeploymentTargetConnectionFactory.CreateInCluster(metadataRevision.Connection.Version + 1),
            metadataRevision.UpdatedAtUtc.AddSeconds(1));
        await ReplaceTargetAsync(connectionRevision);
        var clientAfterConnectionRotation = await provider.GetAsync(target.Id);

        Assert.Same(initialClient, clientAfterRevision);
        Assert.NotSame(clientAfterRevision, clientAfterConnectionRotation);
        Assert.Equal(2, factory.Calls.Count);
        Assert.Equal(
            [
                target.Connection.Version,
                connectionRevision.Connection.Version
            ],
            factory.Calls.Select(call => call.ConnectionVersion));
    }

    [Fact]
    public async Task GetAsync_ReportsMissingDisabledAndUnconfiguredDefaultTargets()
    {
        var disabledTarget = CreateTarget(enabled: false);
        await fixture.DbContext.DeploymentTargets.InsertOneAsync(disabledTarget);
        var factory = new RecordingClientFactory();
        using var provider = CreateProvider(factory);

        var missing = await Assert.ThrowsAsync<DeploymentTargetUnavailableException>(
            () => provider.GetAsync(ObjectId.GenerateNewId().ToString()));
        var disabled = await Assert.ThrowsAsync<DeploymentTargetUnavailableException>(
            () => provider.GetAsync(disabledTarget.Id));
        var defaultNotConfigured = await Assert.ThrowsAsync<DeploymentTargetUnavailableException>(
            () => provider.GetDefaultAsync());

        Assert.Equal(DeploymentTargetUnavailableReason.NotFound, missing.Reason);
        Assert.Equal(DeploymentTargetUnavailableReason.Disabled, disabled.Reason);
        Assert.Equal(DeploymentTargetUnavailableReason.DefaultNotConfigured, defaultNotConfigured.Reason);
        Assert.Empty(factory.Calls);
    }

    [Fact]
    public async Task GetAsync_ResolvesEnabledDegradedTarget()
    {
        var target = CreateTarget(degraded: true);
        await fixture.DbContext.DeploymentTargets.InsertOneAsync(target);
        var factory = new RecordingClientFactory();
        using var provider = CreateProvider(factory);

        var client = await provider.GetAsync(target.Id);

        Assert.Same(Assert.Single(factory.Calls).Client, client);
    }

    [Fact]
    public async Task ReplacedClient_IsNotDisposedUntilProviderIsDisposed()
    {
        var target = CreateTarget();
        await fixture.DbContext.DeploymentTargets.InsertOneAsync(target);
        var factory = new RecordingClientFactory();
        using var provider = CreateProvider(factory);

        await provider.GetAsync(target.Id);
        var updatedTarget = target.ReplaceConnectionCredential(
            target.ClusterIdentity,
            DeploymentTargetConnectionFactory.CreateInCluster(target.Connection.Version + 1),
            target.UpdatedAtUtc.AddSeconds(1));
        await ReplaceTargetAsync(updatedTarget);
        await provider.GetAsync(target.Id);

        Assert.Equal(2, factory.Calls.Count);
        Assert.All(factory.Calls, call => Assert.False(call.DisposalHandler.IsDisposed));

        provider.Dispose();

        Assert.All(factory.Calls, call => Assert.True(call.DisposalHandler.IsDisposed));
    }

    private async Task ReplaceTargetAsync(DeploymentTarget target) =>
        await fixture.DbContext.DeploymentTargets.ReplaceOneAsync(
            existing => existing.Id == target.Id,
            target);

    private KubernetesClientProvider CreateProvider(RecordingClientFactory factory) =>
        new(
            new MongoLoadDeploymentTargetById(fixture.DbContext.DeploymentTargets).InvokeAsync,
            new MongoLoadDefaultDeploymentTargetId(fixture.DbContext.DeploymentTargetSettings)
                .InvokeAsync,
            factory.InvokeAsync);

    private static DeploymentTarget CreateTarget(bool enabled = true, bool degraded = false)
    {
        var id = ObjectId.GenerateNewId().ToString();
        var createdAtUtc = DateTime.UtcNow;
        const long connectionVersion = 1;
        var health = degraded
            ? DeploymentTargetHealth.Degraded(
                connectionVersion,
                createdAtUtc,
                null,
                "probe-failed",
                "The cluster probe failed.")
            : DeploymentTargetHealth.Unknown(connectionVersion);

        return DeploymentTargetFactory.Create(
            $"Target {id[..8]}",
            $"cluster-{id}",
            DeploymentTargetConnectionFactory.CreateInCluster(connectionVersion),
            health,
            DeploymentTargetCapabilities.Unknown(connectionVersion),
            DeploymentTargetTestData.CreateWorkloadConfiguration(),
            enabled,
            acceptsWorkloads: enabled,
            new DeploymentTargetFactory.CreateOptions(id, createdAtUtc));
    }

    private sealed class RecordingClientFactory
    {
        public List<ClientFactoryCall> Calls { get; } = [];

        public ValueTask<Result<
            IKubernetes,
            StoredDeploymentTargetConnectionUnavailable>> InvokeAsync(
            string deploymentTargetId,
            DeploymentTargetConnection connection,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var disposalHandler = new DisposalTrackingHandler();
            IKubernetes client = new Kubernetes(
                new KubernetesClientConfiguration
                {
                    Host = $"https://{deploymentTargetId}.invalid"
                },
                disposalHandler);
            Calls.Add(new ClientFactoryCall(
                deploymentTargetId,
                connection.Version,
                client,
                disposalHandler));
            return ValueTask.FromResult<Result<
                IKubernetes,
                StoredDeploymentTargetConnectionUnavailable>>(new Ok<IKubernetes>(client));
        }
    }

    private sealed record ClientFactoryCall(
        string DeploymentTargetId,
        long ConnectionVersion,
        IKubernetes Client,
        DisposalTrackingHandler DisposalHandler);

    private sealed class DisposalTrackingHandler : DelegatingHandler
    {
        public bool IsDisposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                IsDisposed = true;
            base.Dispose(disposing);
        }
    }
}
