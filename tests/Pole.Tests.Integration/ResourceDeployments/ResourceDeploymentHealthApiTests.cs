using Pole.Features.Core.Multitenancy;
using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.ResourceDeployments.Application.Persistence.Reads;
using Pole.Features.ResourceDeployments.Application.Observation;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Workloads;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.Tenants.Domain;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.ResourceDeployments;

public sealed class ResourceDeploymentHealthApiTests(MongoReplicaSetFixture fixture) : ApiIntegrationTestBase(fixture)
{
    [Fact]
    public async Task Viewer_operations_preserve_partial_states_and_exact_scope_without_native_messages()
    {
        var ready = Create(TestData.TenantId);
        var failed = Create(TestData.TenantId) with
        {
            ResourceGroupId = ready.ResourceGroupId,
            State = new FailedResourceDeploymentState(1, ResourceDeploymentOperation.Apply, "KubernetesApplyFailed", "secret-canary-private-native-message", true, 2, DateTime.UtcNow, DateTime.UtcNow.AddMinutes(1))
        };
        var foreign = Create(ObjectId.GenerateNewId().ToString()) with { ResourceGroupId = ready.ResourceGroupId };
        await Fixture.DbContext.ResourceDeployments.InsertManyAsync([ready, failed, foreign]);
        var tenant = await Fixture.DbContext.Tenants.Find(x => x.Id == TestData.TenantId).SingleAsync();
        await Fixture.DbContext.Tenants.ReplaceOneAsync(x => x.Id == tenant.Id, tenant with
        {
            Members = ImmutableList.Create(TenantFactory.CreateMember(TestData.TenantId, TestData.UserId, TestData.UserEmail, TenantRole.Viewer))
        });
        var uri = $"/api/v1/resource-deployments/operations?resourceGroupId={ready.ResourceGroupId.ToUpperInvariant()}&pageNumber=1&pageSize=1";
        var page = (await Client.GetFromJsonAsync<ResourceDeploymentOperationsPage>(uri))!;
        Assert.Equal(2, page.TotalCount);
        Assert.Equal(1, page.Counts.Applied);
        Assert.Equal(1, page.Counts.Failed);
        Assert.Single(page.Items);
        Assert.DoesNotContain(foreign.Id, await Client.GetStringAsync(uri));
        var exact = $"/api/v1/resource-deployments/operations?resourceType=pole%2Fhealth-test&resourceId={failed.ResourceId}";
        var failure = Assert.Single((await Client.GetFromJsonAsync<ResourceDeploymentOperationsPage>(exact))!.Items);
        Assert.True(failure.Retryable);
        Assert.Equal(2, failure.AttemptCount);
        Assert.Equal("Failed", failure.Phase);
        Assert.DoesNotContain("secret-canary", await Client.GetStringAsync(exact));
        Assert.DoesNotContain("private-native-namespace", await Client.GetStringAsync(exact));
        var observation = (await Client.GetFromJsonAsync<ResourceDeploymentObservationDto>($"/api/v1/resource-deployments/{failed.Id}/observation"))!;
        Assert.Equal("Unsupported", Assert.IsType<Pole.Workloads.UnavailableWorkloadObservation>(observation.Observation).Reason);
        Assert.Equal(HttpStatusCode.NotFound, (await Client.GetAsync($"/api/v1/resource-deployments/{foreign.Id}/observation")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Client.PostAsync($"/api/v1/resource-deployments/{failed.Id}/retry?expectedRevision=1&expectedOperation=Apply", null)).StatusCode);
    }

    [Theory]
    [InlineData("resourceType=invalid&resourceId=507f1f77bcf86cd799439011")]
    [InlineData("resourceType=pole%2Fhealth-test")]
    [InlineData("resourceId=507f1f77bcf86cd799439011")]
    [InlineData("pageSize=51")]
    [InlineData("pageNumber=0")]
    public async Task Operations_reject_unbounded_or_ambiguous_query(string query) =>
        Assert.Equal(HttpStatusCode.BadRequest, (await Client.GetAsync($"/api/v1/resource-deployments/operations?{query}")).StatusCode);

    [Fact]
    public async Task Observation_discards_native_evidence_when_persisted_revision_changes_during_read()
    {
        var deployment = Create(TestData.TenantId);
        await Fixture.DbContext.ResourceDeployments.InsertOneAsync(deployment);
        var fake = new TestObserver(async snapshot =>
        {
            Assert.Equal(deployment.ResolvedPlacement.DeploymentTargetId, snapshot.Placement.DeploymentTargetId);
            Assert.Equal(deployment.TenantId, snapshot.TenantId);
            await Fixture.DbContext.ResourceDeployments.UpdateOneAsync(x => x.Id == deployment.Id, Builders<ResourceDeployment>.Update.Inc(x => x.Revision, 1));
            return new ObservedWorkload(true, true, 1, 1, 1, 1, 1, false, [], [], null);
        });
        using var services = new ServiceCollection().AddSingleton(fake).BuildServiceProvider();
        var persistence = new ResourceDeploymentOperationsPersistence(Fixture.DbContext.ResourceDeployments,
            (_, _, _, _) => Task.FromResult<RegisteredResource?>(null),
            (id, _) => Task.FromResult<LookupDeploymentTargetOutcome>(new DeploymentTargetLookupError(new DeploymentTargetNotFound(id))), TimeProvider.System);
        var catalog = new ResourceDeploymentObserverCatalog([ResourceDeploymentObserverDescriptor.Create<TestObserver>(ResourceTypeId.Create(deployment.ResourceType))]);
        var result = await new ObserveResourceDeployment(persistence, catalog, services, TimeProvider.System).InvokeAsync(deployment, CancellationToken.None);
        Assert.Equal("Superseded", Assert.IsType<UnavailableWorkloadObservation>(result.Observation).Reason);
    }

    private sealed class TestObserver(Func<ResourceDeploymentReconciliationSnapshot, Task<WorkloadObservation>> observe) : IResourceDeploymentObserver
    {
        public Task<WorkloadObservation> ObserveAsync(ResourceDeploymentReconciliationSnapshot deployment, CancellationToken cancellationToken = default) => observe(deployment);
    }

    private static ResourceDeployment Create(string tenantId)
    {
        var now = DateTime.UtcNow;
        var deployment = ResourceDeploymentFactory.Create(tenantId, ResourceTypeId.Create("pole/health-test"), ObjectId.GenerateNewId().ToString(),
            ObjectId.GenerateNewId().ToString(), ObjectId.GenerateNewId().ToString(),
            ResolvedResourcePlacement.Create(ObjectId.GenerateNewId().ToString(), ObjectId.GenerateNewId().ToString(), "private-native-namespace", 1, 1,
                null, null, ResourceDeploymentTestData.Compute(), now), ResourcePlacementCapability.SingleTarget, true,
            new ResourceDeploymentFactory.CreateOptions(ObjectId.GenerateNewId().ToString(), now));
        return deployment with { State = new AppliedResourceDeploymentState(1, now), ObservedGeneration = 1 };
    }
}
