// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Bson;
using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.DeploymentTargets.Domain;
using Pole.Features.ResourceDeployments.Application.Common;
using Pole.Features.ResourceDeployments.Application.Queries;
using Pole.Features.ResourceDeployments.Application.Reconciliation;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Tests.Integration.DeploymentTargets;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.ResourceDeployments;

[Collection(IntegrationTestCollection.Global4)]
public sealed class ResourceDeploymentIsolationProjectionTests(
    MongoReplicaSetFixture fixture
) : MongoIntegrationTestBase(fixture)
{
    public override Task InitializeAsync() => base.InitializeAsync();

    public override Task DisposeAsync() => base.DisposeAsync();

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
        var collection = Fixture.Database.GetCollection<ResourceDeployment>("ResourceDeployments");
        await collection.InsertManyAsync(
        [
            Deployment(tenant, resource, first.Id),
            Deployment(ObjectId.GenerateNewId().ToString(), resource, other.Id),
        ]);
        var requestedTargets = new List<string>();
        var current = first;
        var projection = new ResourceDeploymentIsolationProjection((id, _) =>
        {
            requestedTargets.Add(id);
            Assert.Equal(first.Id, id);
            return Task.FromResult(new LookupDeploymentTargetOutcome(current.ToLookupDto()));
        });
        var read = new MongoLoadResourceDeploymentSet(collection, projection);
        Assert.Equal(
            "EnforcementAttested",
            Assert.Single(
                    await read.InvokeAsync(
                        new LoadResourceDeploymentSetRequest(
                            TenantId: tenant,
                            ResourceType: type,
                            ResourceId: resource)))
                .Isolation.Readiness);
        current = DeploymentTargetNetworkPolicyTests.Attest(first, NetworkPolicyEnforcement.Unsupported, true);
        Assert.Equal(
            "IsolationNotEnforced",
            Assert.Single(
                    await read.InvokeAsync(
                        new LoadResourceDeploymentSetRequest(
                            TenantId: tenant,
                            ResourceType: type,
                            ResourceId: resource)))
                .Isolation.Readiness);
        current = DeploymentTargetNetworkPolicyTests.Attest(first, NetworkPolicyEnforcement.Unknown, false);
        Assert.Equal(
            "EnforcementRequired",
            Assert.Single(
                    await read.InvokeAsync(
                        new LoadResourceDeploymentSetRequest(
                            TenantId: tenant,
                            ResourceType: type,
                            ResourceId: resource)))
                .Isolation.Readiness);
        Assert.Equal(3, requestedTargets.Count);
        Assert.Empty(
            await read.InvokeAsync(
                new LoadResourceDeploymentSetRequest(
                    TenantId: ObjectId.GenerateNewId().ToString(),
                    ResourceType: type,
                    ResourceId: resource)));
        Assert.Equal(3, requestedTargets.Count);
    }

    [Fact]
    public async Task Operations_inventory_projects_current_isolation_once_per_target_after_tenant_filtering()
    {
        var target = DeploymentTargetNetworkPolicyTests.Attest(
                DeploymentTargetNetworkPolicyTests.Target(),
                NetworkPolicyEnforcement.Supported,
                false)
            .ToLookupDto();
        var tenant = ObjectId.GenerateNewId().ToString();
        var resource = ObjectId.GenerateNewId().ToString();
        var collection = Fixture.Database.GetCollection<ResourceDeployment>("ResourceDeployments");
        await collection.InsertManyAsync(
        [
            Deployment(tenant, resource, target.Id),
            Deployment(tenant, resource, target.Id),
            Deployment(ObjectId.GenerateNewId().ToString(), resource, ObjectId.GenerateNewId().ToString()),
        ]);
        var lookups = 0;
        var operations = new ResourceDeploymentOperationsPersistence(
            collection,
            (request, _) => Task.FromResult<RegisteredResource?>(null),
            (id, _) =>
            {
                Assert.Equal(target.Id, id);
                lookups++;
                return Task.FromResult<LookupDeploymentTargetOutcome>(target);
            },
            TimeProvider.System);

        var page = await operations.PageAsync(tenant, null, null, null, 1, 20, default);

        Assert.Equal(2, page.Items.Count);
        var policy = target.NetworkPolicy;
        var expected = new ResourceDeploymentIsolationDto(
            policy.Enforcement.ToString(),
            policy.Readiness.ToString(),
            policy.Reason.ToString(),
            policy.LastCheckedAtUtc);
        Assert.All(page.Items, item => Assert.Equal(expected, item.Deployment.Placement.Isolation));
        Assert.Equal(1, lookups);
    }

    [Fact]
    public async Task Missing_targets_use_the_owner_unknown_policy_and_batch_queries_deduplicate()
    {
        var lookups = 0;
        var projection = new ResourceDeploymentIsolationProjection((id, _) =>
        {
            lookups++;
            return Task.FromResult<LookupDeploymentTargetOutcome>(
                new DeploymentTargetLookupError(new DeploymentTargetNotFound(id)));
        });
        Assert.Empty(await projection.LoadAsync(Array.Empty<string>(), default));
        Assert.Equal(0, lookups);
        var policies = await projection.LoadAsync(new[] { "missing", "missing" }, default);
        var policy = DeploymentTargetNetworkPolicyDto.Unknown;
        Assert.Equal(
            new ResourceDeploymentIsolationDto(
                policy.Enforcement.ToString(),
                policy.Readiness.ToString(),
                policy.Reason.ToString(),
                policy.LastCheckedAtUtc),
            policies["missing"]);
        Assert.Equal(1, lookups);
    }

    [Fact]
    public async Task Unexpected_target_lookup_failures_are_not_reported_as_unknown_isolation()
    {
        var failure = new InvalidOperationException("synthetic lookup failure");
        var projection = new ResourceDeploymentIsolationProjection((_, _) => throw failure);
        Assert.Same(
            failure,
            await Assert.ThrowsAsync<InvalidOperationException>(() => projection.LoadAsync("target", default)));
    }

    private static ResourceDeployment Deployment(
        string tenant,
        string resource,
        string target) => ResourceDeploymentFactory.Create(
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
        ResourceDeploymentFactory.DefaultOptions with
        {
            Id = ObjectId.GenerateNewId().ToString(),
            CreatedAtUtc = DateTime.UtcNow,
        });
}
