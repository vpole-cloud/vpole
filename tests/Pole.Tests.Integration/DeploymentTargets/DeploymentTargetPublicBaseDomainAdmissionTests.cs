using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Application.Connections;
using Pole.Features.DeploymentTargets.Application.Endpoints;
using Pole.Features.DeploymentTargets.Application.Persistence;
using Pole.Features.DeploymentTargets.Application.Persistence.Admission;
using Pole.Features.DeploymentTargets.Application.Persistence.Mutations;
using Pole.Features.DeploymentTargets.Application.Persistence.Reads;
using Pole.Features.DeploymentTargets.Application.Persistence.References;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.DeploymentTargets.Domain;
using Pole.ResultType;
using Pole.Tests.Integration.Infrastructure;
using static Pole.Tests.Integration.Infrastructure.ResultAssertions;
using Pole.Workloads;
using Xunit;

namespace Pole.Tests.Integration.DeploymentTargets;

[Collection(IntegrationTestCollection.Name)]
public sealed class DeploymentTargetPublicBaseDomainAdmissionTests(MongoReplicaSetFixture fixture)
    : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task ConcurrentAncestorAndDescendantAdmissions_AllowExactlyOneTarget()
    {
        var persistence = new MongoPersistCreatedDeploymentTarget(
            fixture.DbContext.DeploymentTargets,
            CreateNoOverlappingPublicBaseDomain(),
            new MongoRunTransaction(fixture.Client).InvokeOutcomeAsync<PersistCreatedDeploymentTargetOutcome>);

        var results = await Task.WhenAll(
            persistence.InvokeAsync(CreateTarget("example.test")),
            persistence.InvokeAsync(CreateTarget("apps.example.test")));

        Assert.Single(results, result => result is DeploymentTarget);
        Assert.Single(results, result => result is PersistCreatedDeploymentTargetError);
        Assert.Equal(
            1,
            await fixture.DbContext.DeploymentTargets.CountDocumentsAsync(
                Builders<DeploymentTarget>.Filter.Empty));
    }

    [Fact]
    public async Task Admission_RejectsOverlappingActiveTargetDomain()
    {
        var persistence = new MongoPersistCreatedDeploymentTarget(
            fixture.DbContext.DeploymentTargets,
            CreateNoOverlappingPublicBaseDomain(),
            new MongoRunTransaction(fixture.Client).InvokeOutcomeAsync<PersistCreatedDeploymentTargetOutcome>);
        var first = await persistence.InvokeAsync(CreateTarget("example.test"));
        Assert.IsType<DeploymentTarget>(first.Value);

        var overlap = await persistence.InvokeAsync(CreateTarget("api.example.test"));

        Assert.True(Assert.IsType<PersistCreatedDeploymentTargetError>(overlap.Value).Value is DeploymentTargetPublicBaseDomainConflict);
    }

    [Fact]
    public async Task Admission_KeepsDomainReservedUntilPendingTargetIsPhysicallyRemoved()
    {
        var persistence = new MongoPersistCreatedDeploymentTarget(
            fixture.DbContext.DeploymentTargets,
            CreateNoOverlappingPublicBaseDomain(),
            new MongoRunTransaction(fixture.Client).InvokeOutcomeAsync<PersistCreatedDeploymentTargetOutcome>);
        var target = CreateTarget("apps.example.test");
        Assert.IsType<DeploymentTarget>((await persistence.InvokeAsync(target)).Value);
        var pending = target.RequestDeletion(target.UpdatedAtUtc.AddSeconds(1));
        await fixture.DbContext.DeploymentTargets.ReplaceOneAsync(
            item => item.Id == target.Id,
            pending);

        var overlap = await persistence.InvokeAsync(CreateTarget("apps.example.test"));

        Assert.True(Assert.IsType<PersistCreatedDeploymentTargetError>(overlap.Value).Value is DeploymentTargetPublicBaseDomainConflict);
    }

    [Fact]
    public async Task TenantAwareAvailability_RequiresPublicIngressBaseDomain()
    {
        var publicTarget = CreateTarget("apps.example.test");
        var privateTarget = CreateTarget(publicBaseDomain: null);
        await fixture.DbContext.DeploymentTargets.InsertManyAsync([publicTarget, privateTarget]);
        var admission = CreateWorkloadPlacementAdmission();
        var request = new DeploymentTargetAdmissionRequest(
            ObjectId.GenerateNewId().ToString(),
            ResourceTypeId.Create("pole/web-service"),
            IsolationScopeId: null,
            ExplicitDeploymentTargetId: null,
            DeploymentTargetRequiredCapabilities.PublicIngress);

        var available = await admission(request);

        var target = Assert.IsType<DeploymentTargetLookupDto>(available.Value);
        Assert.Equal(publicTarget.Id, target.Id);
        Assert.Equal("apps.example.test", target.WorkloadConfiguration.Ingress?.PublicBaseDomain);

        var privateAdmission = await admission(
            request with { ExplicitDeploymentTargetId = privateTarget.Id });
        Assert.True(
            Assert.IsType<DeploymentTargetNewWorkloadAdmissionError>(privateAdmission.Value).Value is DeploymentTargetRequiredCapabilitiesUnavailable);
    }

    [Fact]
    public async Task FunctionRuntimeConnectivity_AdmitsOnlyInClusterTargets()
    {
        var inCluster = CreateTarget(publicBaseDomain: null);
        var remote = CreateTarget(publicBaseDomain: null) with
        {
            Connection = new KubeconfigDeploymentTargetConnection(
                1,
                "remote",
                "https://remote.example.test",
                null!)
        };
        var remoteServiceAccount = CreateTarget(publicBaseDomain: null) with
        {
            Connection = new ServiceAccountDeploymentTargetConnection(
                1,
                "https://service-account.example.test",
                "certificate-authority",
                null!)
        };
        await fixture.DbContext.DeploymentTargets.InsertManyAsync(
            [inCluster, remote, remoteServiceAccount]);
        var admission = CreateWorkloadPlacementAdmission();
        var request = new DeploymentTargetAdmissionRequest(
            ObjectId.GenerateNewId().ToString(),
            ResourceTypeId.Create("pole/function-app"),
            IsolationScopeId: null,
            inCluster.Id,
            DeploymentTargetRequiredCapabilities.FunctionRuntimeConnectivity);

        var admitted = Assert.IsType<DeploymentTargetLookupDto>((await admission(request)).Value);
        Assert.Equal(inCluster.Id, admitted.Id);

        var rejected = await admission(request with { ExplicitDeploymentTargetId = remote.Id });
        Assert.True(
            Assert.IsType<DeploymentTargetNewWorkloadAdmissionError>(rejected.Value).Value is DeploymentTargetRequiredCapabilitiesUnavailable);

        var serviceAccountRejected = await admission(
            request with { ExplicitDeploymentTargetId = remoteServiceAccount.Id });
        Assert.True(
            Assert.IsType<DeploymentTargetNewWorkloadAdmissionError>(serviceAccountRejected.Value).Value is DeploymentTargetRequiredCapabilitiesUnavailable);
    }

    [Fact]
    public async Task PublicIngressAdmission_ReturnsTypedUnavailableCase()
    {
        var privateTarget = CreateTarget(publicBaseDomain: null);
        await fixture.DbContext.DeploymentTargets.InsertOneAsync(privateTarget);
        var admission = CreatePublicIngressAdmission();

        var result = await admission(
            new DeploymentTargetAdmissionRequest(
                ObjectId.GenerateNewId().ToString(),
                ResourceTypeId.Create("pole/web-service"),
                IsolationScopeId: null,
                ExplicitDeploymentTargetId: privateTarget.Id,
                DeploymentTargetRequiredCapabilities.PublicIngress));

        var unavailable = result switch
        {
            DeploymentTargetPublicIngressUnavailable value => value,
            _ => throw new Xunit.Sdk.XunitException(
                $"Expected {nameof(DeploymentTargetPublicIngressUnavailable)}.")
        };
        Assert.Equal(privateTarget.Id, unavailable.DeploymentTargetId);
    }

    [Fact]
    public async Task DomainReservationAdmission_UsesNarrowPublicIngressPath()
    {
        var target = CreateTarget("apps.example.test");
        await fixture.DbContext.DeploymentTargets.InsertOneAsync(target);
        var admission = CreateDomainReservationAdmission();

        var result = await admission(
            ObjectId.GenerateNewId().ToString(),
            target.Id);

        var ingress = Assert.IsType<DeploymentTargetPublicIngressDto>(result.Value);
        Assert.Equal(target.Id, ingress.DeploymentTargetId);
        Assert.Equal("apps.example.test", ingress.PublicBaseDomain);
    }

    [Fact]
    public async Task DomainReservationAdmission_ReturnsProviderOwnedTypedError()
    {
        var target = CreateTarget(publicBaseDomain: null);
        await fixture.DbContext.DeploymentTargets.InsertOneAsync(target);
        var admission = CreateDomainReservationAdmission();

        var result = await admission(
            ObjectId.GenerateNewId().ToString(),
            target.Id);

        var error = Assert.IsType<DeploymentTargetDomainReservationAdmissionError>(result.Value).Value;
        var unavailable = error switch
        {
            DeploymentTargetPublicIngressUnavailable value => value,
            _ => throw new Xunit.Sdk.XunitException(
                $"Expected {nameof(DeploymentTargetPublicIngressUnavailable)}.")
        };
        Assert.Equal(target.Id, unavailable.DeploymentTargetId);
    }

    [Fact]
    public async Task DefaultAdmission_ReturnsTheConfiguredTargetStateError()
    {
        var target = CreateTarget("apps.example.test") with { Enabled = false };
        await fixture.DbContext.DeploymentTargets.InsertOneAsync(target);
        await fixture.DbContext.DeploymentTargetSettings.InsertOneAsync(
            DeploymentTargetSettings.Create(target.Id, target.CreatedAtUtc));

        var result = await CreateDefaultAdmission()();

        var disabled = Assert.IsType<DefaultDeploymentTargetWorkloadAdmissionError>(result.Value).Value switch
        {
            DeploymentTargetDisabled error => error,
            _ => throw new Xunit.Sdk.XunitException(
                "Expected the configured target's disabled admission error.")
        };
        Assert.Equal(target.Id, disabled.DeploymentTargetId);
    }

    [Fact]
    public async Task LookupCapabilities_FailFastForMalformedStoredOrBypassedIds()
    {
        var invalidSettings = new DeploymentTargetSettings(
            DeploymentTargetSettings.SingletonId,
            "not-an-object-id",
            Revision: 1,
            DateTime.UtcNow);
        await fixture.DbContext.DeploymentTargetSettings.InsertOneAsync(invalidSettings);

        await Assert.ThrowsAsync<ArgumentException>(
            () => CreateLookup()("not-an-object-id"));
        await Assert.ThrowsAsync<ArgumentException>(
            () => CreateDefaultLookup()());
    }

    [Fact]
    public async Task PublicBaseDomainChange_IsFencedByActiveDomainZoneReference()
    {
        var target = CreateTarget("apps.example.test");
        await fixture.DbContext.DeploymentTargets.InsertOneAsync(target);
        var references = new MongoApplyDeploymentTargetReferenceChanges(
            fixture.DbContext.DeploymentTargets,
            fixture.DbContext.DeploymentTargetReferences,
            TimeProvider.System);
        var registered = await new MongoRunTransaction(fixture.Client)
            .InvokeOutcomeAsync<ApplyDeploymentTargetReferenceChangesOutcome>(
            async (transaction, cancellationToken) =>
            {
                var result = await references.InvokeAsync(
                new DeploymentTargetReferenceOwner(
                    DeploymentTargetReferenceOwnerTypes.DomainZone,
                    ObjectId.GenerateNewId().ToString()),
                [target.Id],
                [],
                transaction.Session,
                cancellationToken);
                return result is DeploymentTargetReferencesChanged
                    ? transaction.Commit(result)
                    : transaction.Abort(result);
            });
        Assert.IsType<DeploymentTargetReferencesChanged>(registered.Value);
        var current = await fixture.DbContext.DeploymentTargets
            .Find(item => item.Id == target.Id)
            .SingleAsync();
        var handler = new UpdateDeploymentTargetHandler(
            LoadById,
            LoadDefaultId,
            new MongoPersistUpdatedDeploymentTarget(
                fixture.DbContext.DeploymentTargets,
                CreateNoPublicBaseDomainReferences(),
                CreateNoOverlappingPublicBaseDomain(),
                new MongoRunTransaction(fixture.Client).InvokeOutcomeAsync<PersistUpdatedDeploymentTargetOutcome>).InvokeAsync,
            new NoOpClientCache().Invalidate,
            TimeProvider.System);

        var result = await handler.HandleAsync(
            new UpdateDeploymentTargetRequest(
                current.Id,
                new UpdateDeploymentTargetRequestBody(
                    current.DisplayName,
                    current.Enabled,
                    current.AcceptsWorkloads,
                    new DeploymentTargetWorkloadConfigurationRequestBody(
                        new DeploymentTargetIngressConfigurationRequestBody(
                            "traefik",
                            "letsencrypt-prod",
                            "new.example.test")),
                    current.Revision)));

        _ = result switch
        {
            DeploymentTargetPublicBaseDomainReferenced => true,
            _ => throw new Xunit.Sdk.XunitException(
                "Expected the public-base-domain reference rejection.")
        };
    }

    private LookupDeploymentTarget CreateLookup() =>
        new MongoLookupDeploymentTarget(CreateLoadById()).InvokeAsync;

    private LookupDefaultDeploymentTarget CreateDefaultLookup() =>
        new MongoLookupDefaultDeploymentTarget(
            CreateLoadDefaultId(),
            CreateLoadById()).InvokeAsync;

    private AdmitDeploymentTargetForNewWorkload CreateWorkloadPlacementAdmission() =>
        new MongoAdmitDeploymentTargetForNewWorkload(
            CreateWorkloadAdmission(),
            CreateEligibleLoader()).InvokeAsync;

    private AdmitDeploymentTargetPublicIngressForNewWorkload CreatePublicIngressAdmission() =>
        new MongoAdmitDeploymentTargetPublicIngressForNewWorkload(
            CreateWorkloadAdmission(),
            CreateEligibleLoader()).InvokeAsync;

    private AdmitDeploymentTargetPublicIngressForDomainReservation
        CreateDomainReservationAdmission() =>
        new MongoAdmitDeploymentTargetPublicIngressForDomainReservation(
            CreateWorkloadAdmission()).InvokeAsync;

    private AdmitDefaultDeploymentTargetForNewWorkload CreateDefaultAdmission() =>
        new MongoAdmitDefaultDeploymentTargetForNewWorkload(
            CreateLoadDefaultId(),
            CreateLoadById()).InvokeAsync;

    private AdmitDeploymentTargetForWorkload CreateWorkloadAdmission() =>
        new MongoAdmitDeploymentTargetForWorkload(CreateLoadById()).InvokeAsync;

    private LoadEligibleDeploymentTargets CreateEligibleLoader() =>
        new MongoLoadEligibleDeploymentTargets(fixture.DbContext.DeploymentTargets).InvokeAsync;

    private LoadDeploymentTargetById CreateLoadById() =>
        new MongoLoadDeploymentTargetById(fixture.DbContext.DeploymentTargets).InvokeAsync;

    private LoadDefaultDeploymentTargetId CreateLoadDefaultId() =>
        new MongoLoadDefaultDeploymentTargetId(fixture.DbContext.DeploymentTargetSettings)
            .InvokeAsync;

    private EnsureNoOverlappingDeploymentTargetPublicBaseDomain
        CreateNoOverlappingPublicBaseDomain() =>
        new MongoEnsureNoOverlappingDeploymentTargetPublicBaseDomain(
            fixture.DbContext.DeploymentTargets,
            fixture.Database.GetCollection<DeploymentTargetPublicBaseDomainAdmissionFence>(
                DeploymentTargetPublicBaseDomainAdmissionFence.CollectionName)).InvokeAsync;

    private EnsureNoDeploymentTargetPublicBaseDomainReferences
        CreateNoPublicBaseDomainReferences() =>
        new MongoEnsureNoDeploymentTargetPublicBaseDomainReferences(
            fixture.DbContext.DeploymentTargetReferences).InvokeAsync;

    private async Task<DeploymentTarget?> LoadById(
        string id,
        CancellationToken cancellationToken) =>
        await fixture.DbContext.DeploymentTargets
            .Find(item => item.Id == id)
            .FirstOrDefaultAsync(cancellationToken);

    private async Task<string?> LoadDefaultId(CancellationToken cancellationToken) =>
        await fixture.DbContext.DeploymentTargetSettings
            .Find(item => item.Id == DeploymentTargetSettings.SingletonId)
            .Project(item => item.DefaultDeploymentTargetId)
            .FirstOrDefaultAsync(cancellationToken);

    private static DeploymentTarget CreateTarget(string? publicBaseDomain)
    {
        var id = ObjectId.GenerateNewId().ToString();
        var now = DateTime.UtcNow;
        const long connectionVersion = 1;
        return DeploymentTargetFactory.Create(
            "Target",
            $"kubernetes:kube-system:{id}",
            DeploymentTargetConnectionFactory.CreateInCluster(connectionVersion),
            DeploymentTargetHealth.Healthy(connectionVersion, now),
            DeploymentTargetCapabilities.Unknown(connectionVersion),
            DeploymentTargetWorkloadConfiguration.Create(
                DeploymentTargetIngressConfiguration.Create(
                    "traefik",
                    "letsencrypt-prod",
                    publicBaseDomain)),
            enabled: true,
            acceptsWorkloads: true,
            new DeploymentTargetFactory.CreateOptions(id, now)).AttestForTests();
    }

    private sealed class NoOpClientCache
    {
        public void Invalidate(string deploymentTargetId)
        {
        }
    }
}
