using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Application.Connections;
using Pole.Features.DeploymentTargets.Application.Endpoints;
using Pole.Features.DeploymentTargets.Application.Persistence;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.DeploymentTargets.Domain;
using Pole.ResultType;
using Pole.Tests.Integration.Infrastructure;
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
            CreateDomainAdmission(),
            new MongoRunTransaction(fixture.Client).InvokeOutcomeAsync<Result<
                DeploymentTarget,
                PersistCreatedDeploymentTargetError>>);

        var results = await Task.WhenAll(
            persistence.InvokeAsync(CreateTarget("example.test")),
            persistence.InvokeAsync(CreateTarget("apps.example.test")));

        Assert.Single(results, result => result is Ok<DeploymentTarget>);
        Assert.Single(results, result => result is Err<PersistCreatedDeploymentTargetError>);
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
            CreateDomainAdmission(),
            new MongoRunTransaction(fixture.Client).InvokeOutcomeAsync<Result<
                DeploymentTarget,
                PersistCreatedDeploymentTargetError>>);
        var first = await persistence.InvokeAsync(CreateTarget("example.test"));
        _ = AssertSuccess(first);

        var overlap = await persistence.InvokeAsync(CreateTarget("api.example.test"));

        Assert.True(AssertFailure(overlap) is DeploymentTargetPublicBaseDomainConflict);
    }

    [Fact]
    public async Task Admission_KeepsDomainReservedUntilPendingTargetIsPhysicallyRemoved()
    {
        var persistence = new MongoPersistCreatedDeploymentTarget(
            fixture.DbContext.DeploymentTargets,
            CreateDomainAdmission(),
            new MongoRunTransaction(fixture.Client).InvokeOutcomeAsync<Result<
                DeploymentTarget,
                PersistCreatedDeploymentTargetError>>);
        var target = CreateTarget("apps.example.test");
        _ = AssertSuccess(await persistence.InvokeAsync(target));
        var pending = target.RequestDeletion(target.UpdatedAtUtc.AddSeconds(1));
        await fixture.DbContext.DeploymentTargets.ReplaceOneAsync(
            item => item.Id == target.Id,
            pending);

        var overlap = await persistence.InvokeAsync(CreateTarget("apps.example.test"));

        Assert.True(AssertFailure(overlap) is DeploymentTargetPublicBaseDomainConflict);
    }

    [Fact]
    public async Task TenantAwareAvailability_RequiresPublicIngressBaseDomain()
    {
        var publicTarget = CreateTarget("apps.example.test");
        var privateTarget = CreateTarget(publicBaseDomain: null);
        await fixture.DbContext.DeploymentTargets.InsertManyAsync([publicTarget, privateTarget]);
        var lookup = CreateLookup();
        var request = new DeploymentTargetAdmissionRequest(
            ObjectId.GenerateNewId().ToString(),
            ResourceTypeId.Create("pole/web-service"),
            IsolationScopeId: null,
            ExplicitDeploymentTargetId: null,
            DeploymentTargetRequiredCapabilities.PublicIngress);

        var available = await lookup.AdmitForWorkloadPlacementAsync(request);

        var target = AssertSuccess(available);
        Assert.Equal(publicTarget.Id, target.Id);
        Assert.Equal("apps.example.test", target.WorkloadConfiguration.Ingress?.PublicBaseDomain);

        var privateAdmission = await lookup.AdmitForWorkloadPlacementAsync(
            request with { ExplicitDeploymentTargetId = privateTarget.Id });
        Assert.True(
            AssertFailure(privateAdmission) is DeploymentTargetRequiredCapabilitiesUnavailable);
    }

    [Fact]
    public async Task PublicIngressAdmission_ReturnsTypedUnavailableCase()
    {
        var privateTarget = CreateTarget(publicBaseDomain: null);
        await fixture.DbContext.DeploymentTargets.InsertOneAsync(privateTarget);
        var lookup = CreateLookup();
        AdmitDeploymentTargetPublicIngressForNewWorkload admission =
            lookup.AdmitForNewWorkloadAsync;

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
        var lookup = CreateLookup();
        AdmitDeploymentTargetPublicIngressForDomainReservation admission =
            lookup.AdmitForDomainReservationAsync;

        var result = await admission(
            ObjectId.GenerateNewId().ToString(),
            target.Id);

        Assert.True(result.TryGetValue(out var ingress, out _));
        Assert.Equal(target.Id, ingress.DeploymentTargetId);
        Assert.Equal("apps.example.test", ingress.PublicBaseDomain);
    }

    [Fact]
    public async Task DomainReservationAdmission_ReturnsProviderOwnedTypedError()
    {
        var target = CreateTarget(publicBaseDomain: null);
        await fixture.DbContext.DeploymentTargets.InsertOneAsync(target);
        var lookup = CreateLookup();
        AdmitDeploymentTargetPublicIngressForDomainReservation admission =
            lookup.AdmitForDomainReservationAsync;

        var result = await admission(
            ObjectId.GenerateNewId().ToString(),
            target.Id);

        Assert.False(result.TryGetValue(out _, out var error));
        var unavailable = error switch
        {
            DeploymentTargetPublicIngressUnavailable value => value,
            _ => throw new Xunit.Sdk.XunitException(
                $"Expected {nameof(DeploymentTargetPublicIngressUnavailable)}.")
        };
        Assert.Equal(target.Id, unavailable.DeploymentTargetId);
    }

    [Fact]
    public async Task PublicBaseDomainChange_IsFencedByActiveDomainZoneReference()
    {
        var target = CreateTarget("apps.example.test");
        await fixture.DbContext.DeploymentTargets.InsertOneAsync(target);
        var references = new DeploymentTargetReferenceAdmission(
            fixture.DbContext.DeploymentTargets,
            fixture.DbContext.DeploymentTargetReferences,
            TimeProvider.System);
        var registered = await new MongoRunTransaction(fixture.Client)
            .InvokeOutcomeAsync<Result<Unit, DeploymentTargetReferenceChangeRejected>>(
            async (transaction, cancellationToken) =>
            {
                var result = await references.ApplyChangesOutcomeAsync(
                new DeploymentTargetReferenceOwner(
                    DeploymentTargetReferenceOwnerTypes.DomainZone,
                    ObjectId.GenerateNewId().ToString()),
                [target.Id],
                [],
                transaction.Session,
                cancellationToken);
                return result is Ok<Unit>
                    ? transaction.Commit(result)
                    : transaction.Abort(result);
            });
        _ = AssertSuccess(registered);
        var current = await fixture.DbContext.DeploymentTargets
            .Find(item => item.Id == target.Id)
            .SingleAsync();
        var handler = new UpdateDeploymentTargetHandler(
            LoadById,
            LoadDefaultId,
            new MongoPersistUpdatedDeploymentTarget(
                fixture.DbContext.DeploymentTargets,
                CreateDomainAdmission(),
                new MongoRunTransaction(fixture.Client).InvokeOutcomeAsync<Result<
                    DeploymentTarget,
                    PersistUpdatedDeploymentTargetError>>).InvokeAsync,
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

        var error = result switch
        {
            UpdateDeploymentTargetError value => value,
            _ => throw new Xunit.Sdk.XunitException("Expected an update error.")
        };
        var persistenceError = error switch
        {
            PersistUpdatedDeploymentTargetError value => value,
            _ => throw new Xunit.Sdk.XunitException("Expected a persistence error.")
        };
        Assert.True(persistenceError is DeploymentTargetPublicBaseDomainReferenced);
    }

    private DeploymentTargetAdmissionQueries CreateLookup() =>
        new(
            fixture.DbContext.DeploymentTargets,
            fixture.DbContext.DeploymentTargetSettings);

    private DeploymentTargetDomainAdmission CreateDomainAdmission() =>
        new(
            fixture.DbContext.DeploymentTargets,
            fixture.DbContext.DeploymentTargetReferences,
            fixture.Database.GetCollection<DeploymentTargetPublicBaseDomainAdmissionFence>(
                DeploymentTargetPublicBaseDomainAdmissionFence.CollectionName));

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

    private static T AssertSuccess<T, TError>(Result<T, TError> result)
        where T : notnull
        where TError : notnull =>
        result switch
        {
            Ok<T> success => success.Value,
            Err<TError> error => throw new Xunit.Sdk.XunitException(
                $"Expected success, got {error.Value.GetType().Name}."),
            _ => throw new Xunit.Sdk.XunitException("Result returned an unsupported case.")
        };

    private static TError AssertFailure<T, TError>(Result<T, TError> result)
        where T : notnull
        where TError : notnull =>
        result switch
        {
            Err<TError> error => error.Value,
            Ok<T> => throw new Xunit.Sdk.XunitException("Expected failure, got success."),
            _ => throw new Xunit.Sdk.XunitException("Result returned an unsupported case.")
        };

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
            new DeploymentTargetFactory.CreateOptions(id, now));
    }

    private sealed class NoOpClientCache
    {
        public void Invalidate(string deploymentTargetId)
        {
        }
    }
}
