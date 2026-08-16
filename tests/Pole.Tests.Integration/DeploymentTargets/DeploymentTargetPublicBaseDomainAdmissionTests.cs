using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Application.Connections;
using Pole.Features.DeploymentTargets.Application.Endpoints;
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
        var persistence = new DeploymentTargetPersistence(
            fixture.DbContext,
            new DeploymentTargetDomainAdmission(fixture.DbContext));

        var results = await Task.WhenAll(
            persistence.InsertAsync(CreateTarget("example.test")),
            persistence.InsertAsync(CreateTarget("apps.example.test")));

        Assert.Single(results, result => result is ISuccess);
        Assert.Single(results, result => result is not ISuccess);
        Assert.Equal(
            1,
            await fixture.DbContext.DeploymentTargets.CountDocumentsAsync(
                Builders<DeploymentTarget>.Filter.Empty));
    }

    [Fact]
    public async Task Admission_RejectsOverlappingActiveTargetDomain()
    {
        var persistence = new DeploymentTargetPersistence(
            fixture.DbContext,
            new DeploymentTargetDomainAdmission(fixture.DbContext));
        var first = await persistence.InsertAsync(CreateTarget("example.test"));
        Assert.IsAssignableFrom<ISuccess>(first);

        var overlap = await persistence.InsertAsync(CreateTarget("api.example.test"));

        Assert.False(overlap.TryGetValue(out _, out var failure));
        Assert.IsType<ConflictFailure>(failure);
    }

    [Fact]
    public async Task Admission_KeepsDomainReservedUntilPendingTargetIsPhysicallyRemoved()
    {
        var persistence = new DeploymentTargetPersistence(
            fixture.DbContext,
            new DeploymentTargetDomainAdmission(fixture.DbContext));
        var target = CreateTarget("apps.example.test");
        Assert.IsAssignableFrom<ISuccess>(await persistence.InsertAsync(target));
        var pending = target.RequestDeletion(target.UpdatedAtUtc.AddSeconds(1));
        await fixture.DbContext.DeploymentTargets.ReplaceOneAsync(
            item => item.Id == target.Id,
            pending);

        var overlap = await persistence.InsertAsync(CreateTarget("apps.example.test"));

        Assert.False(overlap.TryGetValue(out _, out var failure));
        Assert.IsType<ConflictFailure>(failure);
    }

    [Fact]
    public async Task TenantAwareAvailability_RequiresPublicIngressBaseDomain()
    {
        var publicTarget = CreateTarget("apps.example.test");
        var privateTarget = CreateTarget(publicBaseDomain: null);
        await fixture.DbContext.DeploymentTargets.InsertManyAsync([publicTarget, privateTarget]);
        var lookup = new DeploymentTargetLookup(fixture.DbContext);
        var request = new DeploymentTargetAdmissionRequest(
            ObjectId.GenerateNewId().ToString(),
            ResourceTypeId.Create("pole/web-service"),
            IsolationScopeId: null,
            ExplicitDeploymentTargetId: null,
            DeploymentTargetRequiredCapabilities.PublicIngress);

        var available = await lookup.ListEligibleForNewWorkloadAsync(request);

        Assert.True(available.TryGetValue(out var targets, out var failure), failure?.Message);
        var target = Assert.Single(targets);
        Assert.Equal(publicTarget.Id, target.Id);
        Assert.Equal("apps.example.test", target.WorkloadConfiguration.Ingress?.PublicBaseDomain);

        var privateAdmission = await lookup.ResolveForNewWorkloadAsync(
            request with { ExplicitDeploymentTargetId = privateTarget.Id });
        Assert.False(privateAdmission.TryGetValue(out _, out var privateFailure));
        Assert.IsType<SemanticValidationFailure>(privateFailure);
    }

    [Fact]
    public async Task PublicIngressAdmission_ReturnsTypedUnavailableCase()
    {
        var privateTarget = CreateTarget(publicBaseDomain: null);
        await fixture.DbContext.DeploymentTargets.InsertOneAsync(privateTarget);
        IDeploymentTargetPublicIngressAdmission admission =
            new DeploymentTargetLookup(fixture.DbContext);

        var result = await admission.AdmitForNewWorkloadAsync(
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
        IDeploymentTargetPublicIngressAdmission admission =
            new DeploymentTargetLookup(fixture.DbContext);

        var result = await admission.AdmitForDomainReservationAsync(
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
        IDeploymentTargetPublicIngressAdmission admission =
            new DeploymentTargetLookup(fixture.DbContext);

        var result = await admission.AdmitForDomainReservationAsync(
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
        var references = new DeploymentTargetReferenceAdmission(fixture.DbContext, TimeProvider.System);
        var registered = await fixture.DbContext.RunInTransactionAsync(
            (session, cancellationToken) => references.RegisterAsync(
                target.Id,
                new DeploymentTargetReferenceOwner(
                    DeploymentTargetReferenceOwnerTypes.DomainZone,
                    ObjectId.GenerateNewId().ToString()),
                session,
                cancellationToken));
        Assert.IsAssignableFrom<ISuccess>(registered);
        var current = await fixture.DbContext.DeploymentTargets
            .Find(item => item.Id == target.Id)
            .SingleAsync();
        var handler = new UpdateDeploymentTargetHandler(
            fixture.DbContext,
            new DeploymentTargetReader(fixture.DbContext),
            new DeploymentTargetDomainAdmission(fixture.DbContext),
            new NoOpClientCache(),
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

        Assert.False(result.TryGetValue(out _, out var failure));
        Assert.IsType<ConflictFailure>(failure);
    }

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

    private sealed class NoOpClientCache : IDeploymentTargetClientCache
    {
        public void Invalidate(string deploymentTargetId)
        {
        }
    }
}
