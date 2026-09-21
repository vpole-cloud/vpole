using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.DeploymentTargets.Domain;
using Pole.Features.ResourceDeployments.Application.Common;
using Pole.Features.ResourceDeployments.Application.Persistence.Reads;
using Pole.Features.ResourceDeployments.Domain;
using Pole.ResultType;
using Pole.Tests.Integration.DeploymentTargets;
using Pole.Tests.Integration.Infrastructure;
using Pole.Workloads;
using Xunit;

namespace Pole.Tests.Integration.ResourceDeployments;

[Collection(IntegrationTestCollection.Name)]
public sealed class ResourceDeploymentIsolationProjectionTests(
    MongoReplicaSetFixture fixture)
    : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task ReadsObserveTargetChanges_AndNeverResolveOtherTenantsPlacements()
    {
        var first = DeploymentTargetNetworkPolicyTests.Attest(
            DeploymentTargetNetworkPolicyTests.Target(),
            NetworkPolicyEnforcement.Supported,
            false);
        var other = DeploymentTargetNetworkPolicyTests.Target();
        var tenant = ObjectId.GenerateNewId().ToString();
        var resource = ObjectId.GenerateNewId().ToString();
        var type = ResourceTypeId.Create("pole/web-service");
        var collection = fixture.Database.GetCollection<ResourceDeployment>("ResourceDeployments");
        await collection.InsertManyAsync(
        [
            Deployment(tenant, resource, first.Id),
            Deployment(ObjectId.GenerateNewId().ToString(), resource, other.Id)
        ]);
        var requestedTargets = new List<string>();
        var current = first;
        var projection = new ResourceDeploymentIsolationProjection((id, _) =>
        {
            requestedTargets.Add(id);
            Assert.Equal(first.Id, id);
            return Task.FromResult<Result<DeploymentTargetLookupDto, DeploymentTargetLookupError>>(
                new Ok<DeploymentTargetLookupDto>(current.ToLookupDto()));
        });
        var read = new MongoLoadResourceDeploymentSet(collection, projection);
        Assert.Equal(
            "EnforcementAttested",
            Assert.Single(await read.InvokeAsync(tenant, type, resource)).Isolation.Readiness);
        current = DeploymentTargetNetworkPolicyTests.Attest(
            first,
            NetworkPolicyEnforcement.Unsupported,
            true);
        Assert.Equal(
            "IsolationNotEnforced",
            Assert.Single(await read.InvokeAsync(tenant, type, resource)).Isolation.Readiness);
        current = DeploymentTargetNetworkPolicyTests.Attest(first, NetworkPolicyEnforcement.Unknown, false);
        Assert.Equal(
            "EnforcementRequired",
            Assert.Single(await read.InvokeAsync(tenant, type, resource)).Isolation.Readiness);
        Assert.Equal(3, requestedTargets.Count);
        Assert.Empty(await read.InvokeAsync(ObjectId.GenerateNewId().ToString(), type, resource));
        Assert.Equal(3, requestedTargets.Count);
    }

    private static ResourceDeployment Deployment(
        string tenant,
        string resource,
        string target) =>
        ResourceDeploymentFactory.Create(
            tenant,
            ResourceTypeId.Create("pole/web-service"),
            resource,
            ObjectId.GenerateNewId().ToString(),
            ObjectId.GenerateNewId().ToString(),
            ResolvedResourcePlacement.Create(
                ObjectId.GenerateNewId().ToString(),
                target,
                "private-namespace",
                1,
                1,
                null,
                null,
                ResourceDeploymentTestData.Compute(),
                DateTime.UtcNow),
            ResourcePlacementCapability.SingleTarget,
            true,
            new(ObjectId.GenerateNewId().ToString(), DateTime.UtcNow));
}
