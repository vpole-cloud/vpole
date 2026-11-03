using System.Collections.Immutable;
using System.Net;
using Pole.Tests.Integration.Http;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.RedisServices.Contracts;
using Pole.Features.RedisServices.Domain;
using Pole.Features.Core.Multitenancy;
using Pole.Features.ResourceConnections.Application.Endpoints;
using Pole.Features.ResourceConnections.Domain;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.ResourceGroups.Domain;
using Pole.Features.Request;
using Pole.Features.Tenants.Domain;
using Pole.Tests.Integration.Infrastructure;
using Pole.Tests.Integration.ResourceDeployments;
using Pole.Workloads;
using Xunit;

namespace Pole.Tests.Integration.ResourceConnections;

public sealed class ResourceConnectionCreateApiTests(MongoReplicaSetFixture fixture)
    : ApiIntegrationTestBase(fixture)
{
    private static readonly ResourceTypeId SourceType = ResourceTypeId.Create("pole/web-service");
    private const string CreatedEventType =
        "pole.resource-connections/v1/resource-connection-created";
    private const string DeletionEventType =
        "pole.resource-connections/v1/resource-connection-deletion-requested";

    [Fact]
    public async Task Create_AcceptsCrossGroupPlacementsWithSnapshotsAndAtomicOutbox()
    {
        var sourceId = Id();
        var destinationId = Id();
        var targetId = Id();
        var sourceGroup = await SeedReferenceAsync(SourceType, sourceId, "source-group");
        var destinationGroup = await SeedReferenceAsync(
            RedisConnectionEndpoints.ResourceType,
            destinationId,
            "destination-group");
        var sourceDeployment = await SeedDeploymentAsync(
            SourceType, sourceId, sourceGroup, targetId, "source-namespace");
        var destinationDeployment = await SeedDeploymentAsync(
            RedisConnectionEndpoints.ResourceType,
            destinationId,
            destinationGroup,
            targetId,
            "destination-namespace");

        using var response = await Client.PostAsJsonAsync(
            "/api/v1/resource-connections",
            Body(sourceId, destinationId));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var accepted = await response.Content.ReadFromJsonAsync<CreateResourceConnectionResponse>();
        Assert.NotNull(accepted);
        Assert.Equal("Pending", accepted.Status);
        Assert.Equal([targetId], accepted.TargetIds);
        var connection = await Connections.Find(item => item.Id == accepted.Id).SingleAsync();
        var active = Assert.IsType<ActiveResourceConnection>(connection.Lifecycle);
        var snapshot = Assert.Single(active.Targets).Snapshot;
        Assert.Equal(sourceDeployment.Id, Assert.Single(snapshot.Sources).ResourceDeploymentId);
        Assert.Equal("source-namespace", snapshot.Sources[0].Namespace);
        Assert.Equal(destinationDeployment.Id, snapshot.DestinationResourceDeploymentId);
        Assert.Equal("destination-namespace", snapshot.DestinationNamespace);
        Assert.Equal(6379, snapshot.DestinationPort);
        Assert.Equal(1, await Fixture.DbContext.CountOutboxMessagesAsync(CreatedEventType));
        Assert.Equal(1, (await Fixture.DbContext.ResourceRegistrations
            .Find(item => item.ResourceId == sourceId)
            .SingleAsync()).ReferenceFence);
        Assert.Equal(1, (await Fixture.DbContext.ResourceRegistrations
            .Find(item => item.ResourceId == destinationId)
            .SingleAsync()).ReferenceFence);

        using var duplicate = await Client.PostAsJsonAsync(
            "/api/v1/resource-connections",
            Body(sourceId, destinationId));
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal(1, await Connections.CountDocumentsAsync(
            item => item.TenantId == TestData.TenantId));
        Assert.Equal(1, await Fixture.DbContext.CountOutboxMessagesAsync(CreatedEventType));
    }

    [Fact]
    public async Task Create_RejectsMissingMatchingDestinationWithoutWrites()
    {
        var sourceId = Id();
        var destinationId = Id();
        var sourceGroup = await SeedReferenceAsync(SourceType, sourceId, "source-group");
        var destinationGroup = await SeedReferenceAsync(
            RedisConnectionEndpoints.ResourceType,
            destinationId,
            "destination-group");
        await SeedDeploymentAsync(SourceType, sourceId, sourceGroup, Id(), "source-namespace");
        await SeedDeploymentAsync(
            RedisConnectionEndpoints.ResourceType,
            destinationId,
            destinationGroup,
            Id(),
            "destination-namespace");

        using var response = await Client.PostAsJsonAsync(
            "/api/v1/resource-connections",
            Body(sourceId, destinationId));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(0, await Connections.CountDocumentsAsync(
            item => item.TenantId == TestData.TenantId));
        Assert.Equal(0, await Fixture.DbContext.CountOutboxMessagesAsync(CreatedEventType));
        Assert.Equal(0, (await Fixture.DbContext.ResourceRegistrations
            .Find(item => item.ResourceId == sourceId)
            .SingleAsync()).ReferenceFence);
        Assert.Equal(0, (await Fixture.DbContext.ResourceRegistrations
            .Find(item => item.ResourceId == destinationId)
            .SingleAsync()).ReferenceFence);
    }

    [Fact]
    public async Task Create_ResolvesEverySourceTargetIntoOnePendingPolicyTarget()
    {
        var sourceId = Id();
        var destinationId = Id();
        var firstTarget = Id();
        var secondTarget = Id();
        var sourceGroup = await SeedReferenceAsync(SourceType, sourceId, "source-group");
        var destinationGroup = await SeedReferenceAsync(
            RedisConnectionEndpoints.ResourceType,
            destinationId,
            "destination-group");
        await SeedDeploymentAsync(SourceType, sourceId, sourceGroup, firstTarget, "source-first");
        await SeedDeploymentAsync(SourceType, sourceId, sourceGroup, secondTarget, "source-second");
        await SeedDeploymentAsync(
            RedisConnectionEndpoints.ResourceType,
            destinationId,
            destinationGroup,
            firstTarget,
            "destination-first");
        await SeedDeploymentAsync(
            RedisConnectionEndpoints.ResourceType,
            destinationId,
            destinationGroup,
            secondTarget,
            "destination-second");

        using var response = await Client.PostAsJsonAsync(
            "/api/v1/resource-connections",
            Body(sourceId, destinationId));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var connection = await Connections.Find(item => item.Name == "orders-cache").SingleAsync();
        var active = Assert.IsType<ActiveResourceConnection>(connection.Lifecycle);
        Assert.Equal(2, active.Targets.Count);
        Assert.Equal(
            new[] { firstTarget, secondTarget }.OrderBy(id => id),
            active.Targets.Select(target => target.Snapshot.TargetId).OrderBy(id => id));
        Assert.All(active.Targets, target => Assert.IsType<PendingConnectionApply>(target.Status));
        Assert.Equal(1, await Fixture.DbContext.CountOutboxMessagesAsync(CreatedEventType));
    }

    [Fact]
    public async Task Create_RejectsCrossTenantDestinationAsMissing()
    {
        var sourceId = Id();
        var destinationId = Id();
        var sourceGroup = await SeedReferenceAsync(SourceType, sourceId, "source-group");
        var foreignTenant = Id();
        await SeedReferenceAsync(
            RedisConnectionEndpoints.ResourceType,
            destinationId,
            "foreign-group",
            foreignTenant);
        await SeedDeploymentAsync(SourceType, sourceId, sourceGroup, Id(), "source-namespace");

        using var response = await Client.PostAsJsonAsync(
            "/api/v1/resource-connections",
            Body(sourceId, destinationId));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, await Connections.CountDocumentsAsync(
            item => item.TenantId == TestData.TenantId));
        Assert.Equal(0, await Fixture.DbContext.CountOutboxMessagesAsync(CreatedEventType));
    }

    [Fact]
    public async Task Create_RejectsEmptySourcePlacementWithoutWrites()
    {
        var sourceId = Id();
        var destinationId = Id();
        await SeedReferenceAsync(SourceType, sourceId, "source-group");
        await SeedReferenceAsync(
            RedisConnectionEndpoints.ResourceType,
            destinationId,
            "destination-group");

        using var response = await Client.PostAsJsonAsync(
            "/api/v1/resource-connections",
            Body(sourceId, destinationId));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(0, await Connections.CountDocumentsAsync(
            item => item.TenantId == TestData.TenantId));
        Assert.Equal(0, await Fixture.DbContext.CountOutboxMessagesAsync(CreatedEventType));
    }

    [Fact]
    public async Task Create_RejectsAmbiguousDestinationOnOneSourceTargetWithoutWrites()
    {
        var sourceId = Id();
        var destinationId = Id();
        var targetId = Id();
        var sourceGroup = await SeedReferenceAsync(SourceType, sourceId, "source-group");
        var destinationGroup = await SeedReferenceAsync(
            RedisConnectionEndpoints.ResourceType,
            destinationId,
            "destination-group");
        await SeedDeploymentAsync(SourceType, sourceId, sourceGroup, targetId, "source-namespace");
        await SeedDeploymentAsync(
            RedisConnectionEndpoints.ResourceType,
            destinationId,
            destinationGroup,
            targetId,
            "destination-namespace-one");
        await SeedDeploymentAsync(
            RedisConnectionEndpoints.ResourceType,
            destinationId,
            destinationGroup,
            targetId,
            "destination-namespace-two");

        using var response = await Client.PostAsJsonAsync(
            "/api/v1/resource-connections",
            Body(sourceId, destinationId));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(0, await Connections.CountDocumentsAsync(
            item => item.TenantId == TestData.TenantId));
        Assert.Equal(0, await Fixture.DbContext.CountOutboxMessagesAsync(CreatedEventType));
    }

    [Fact]
    public async Task Create_RequiresMemberOrHigherTenantRole()
    {
        var sourceId = Id();
        var destinationId = Id();
        var targetId = Id();
        var sourceGroup = await SeedReferenceAsync(SourceType, sourceId, "source-group");
        var destinationGroup = await SeedReferenceAsync(
            RedisConnectionEndpoints.ResourceType,
            destinationId,
            "destination-group");
        await SeedDeploymentAsync(SourceType, sourceId, sourceGroup, targetId, "source-namespace");
        await SeedDeploymentAsync(
            RedisConnectionEndpoints.ResourceType,
            destinationId,
            destinationGroup,
            targetId,
            "destination-namespace");

        await SetTenantRoleAsync(TenantRole.Viewer);
        using var rejected = await Client.PostAsJsonAsync(
            "/api/v1/resource-connections",
            Body(sourceId, destinationId, "viewer-link"));
        Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
        foreach (var role in new[] { TenantRole.Member, TenantRole.Admin, TenantRole.Owner })
        {
            await SetTenantRoleAsync(role);
            using var accepted = await Client.PostAsJsonAsync(
                "/api/v1/resource-connections",
                Body(sourceId, destinationId, $"{role.ToString().ToLowerInvariant()}-link"));
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        }
    }

    [Fact]
    public async Task Create_RejectsUnpublishedSourceAndEndpointWithoutWrites()
    {
        var sourceId = Id();
        var destinationId = Id();
        await SeedReferenceAsync(SourceType, sourceId, "source-group");
        await SeedReferenceAsync(
            RedisConnectionEndpoints.ResourceType,
            destinationId,
            "destination-group");
        using var unsupportedSource = await Client.PostAsJsonAsync(
            "/api/v1/resource-connections",
            Body(sourceId, destinationId) with
            {
                SourceResourceType = "third-party/widget"
            });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unsupportedSource.StatusCode);
        using var unsupportedEndpoint = await Client.PostAsJsonAsync(
            "/api/v1/resource-connections",
            Body(sourceId, destinationId) with
            {
                EndpointId = "arbitrary-port"
            });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unsupportedEndpoint.StatusCode);
        Assert.Equal(0, await Connections.CountDocumentsAsync(
            item => item.TenantId == TestData.TenantId));
        Assert.Equal(0, await Fixture.DbContext.CountOutboxMessagesAsync(CreatedEventType));
        Assert.Equal(0, (await Fixture.DbContext.ResourceRegistrations
            .Find(item => item.ResourceId == sourceId)
            .SingleAsync()).ReferenceFence);
        Assert.Equal(0, (await Fixture.DbContext.ResourceRegistrations
            .Find(item => item.ResourceId == destinationId)
            .SingleAsync()).ReferenceFence);
    }

    [Fact]
    public async Task GetListAndDelete_ExposePendingThenDeletingAndKeepCleanupSnapshot()
    {
        var sourceId = Id();
        var destinationId = Id();
        var targetId = Id();
        var sourceGroup = await SeedReferenceAsync(SourceType, sourceId, "source-group");
        var destinationGroup = await SeedReferenceAsync(
            RedisConnectionEndpoints.ResourceType,
            destinationId,
            "destination-group");
        await SeedDeploymentAsync(SourceType, sourceId, sourceGroup, targetId, "source-namespace");
        await SeedDeploymentAsync(
            RedisConnectionEndpoints.ResourceType,
            destinationId,
            destinationGroup,
            targetId,
            "destination-namespace");
        using var created = await Client.PostAsJsonAsync(
            "/api/v1/resource-connections",
            Body(sourceId, destinationId));
        var accepted = await created.Content.ReadFromJsonAsync<CreateResourceConnectionResponse>();
        Assert.NotNull(accepted);

        await SetTenantRoleAsync(TenantRole.Viewer);
        using var read = await Client.GetAsync($"/api/v1/resource-connections/{accepted.Id}");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        var detail = await read.Content.ReadFromJsonAsync<ResourceConnectionDto>();
        Assert.NotNull(detail);
        Assert.Equal("Pending", detail.Status);
        var target = Assert.Single(detail.Targets);
        Assert.Equal(targetId, target.TargetId);
        Assert.Equal("destination-group-svc.destination-namespace.svc.cluster.local", target.Host);
        Assert.Equal(6379, target.Port);

        using var listed = await Client.GetAsync("/api/v1/resource-connections?pageNumber=1&pageSize=10");
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        var page = await listed.Content.ReadFromJsonAsync<PaginatedResponse<ResourceConnectionDto>>();
        Assert.NotNull(page);
        Assert.Equal(1, page.TotalCount);
        Assert.Single(page.Items);

        using var sourceFiltered = await Client.GetAsync($"/api/v1/resource-connections?pageNumber=1&pageSize=10&sourceResourceType=pole/web-service&sourceResourceId={sourceId}");
        Assert.Equal(HttpStatusCode.OK, sourceFiltered.StatusCode);
        Assert.Equal(accepted.Id, Assert.Single((await sourceFiltered.Content.ReadFromJsonAsync<PaginatedResponse<ResourceConnectionDto>>())!.Items).Id);
        using var unrelatedSource = await Client.GetAsync($"/api/v1/resource-connections?pageNumber=1&pageSize=10&sourceResourceType=pole/web-service&sourceResourceId={Id()}");
        Assert.Equal(0, (await unrelatedSource.Content.ReadFromJsonAsync<PaginatedResponse<ResourceConnectionDto>>())!.TotalCount);
        using var incompleteFilter = await Client.GetAsync($"/api/v1/resource-connections?pageNumber=1&pageSize=10&sourceResourceId={sourceId}");
        Assert.Equal(HttpStatusCode.BadRequest, incompleteFilter.StatusCode);

        using var deniedDelete = await Client.DeleteAsync($"/api/v1/resource-connections/{accepted.Id}");
        Assert.Equal(HttpStatusCode.Forbidden, deniedDelete.StatusCode);
        await SetTenantRoleAsync(TenantRole.Member);
        using var deletion = await Client.DeleteAsync($"/api/v1/resource-connections/{accepted.Id}");
        Assert.Equal(HttpStatusCode.Accepted, deletion.StatusCode);
        var persisted = await Connections.Find(item => item.Id == accepted.Id).SingleAsync();
        var cleanup = Assert.IsType<DeletingResourceConnection>(persisted.Lifecycle);
        Assert.Equal("source-namespace", Assert.Single(cleanup.Targets).Snapshot.Sources[0].Namespace);
        Assert.Equal(1, await Fixture.DbContext.CountOutboxMessagesAsync(DeletionEventType));

        using var repeated = await Client.DeleteAsync($"/api/v1/resource-connections/{accepted.Id}");
        Assert.Equal(HttpStatusCode.Accepted, repeated.StatusCode);
        Assert.Equal(1, await Fixture.DbContext.CountOutboxMessagesAsync(DeletionEventType));
        using var deletingRead = await Client.GetAsync($"/api/v1/resource-connections/{accepted.Id}");
        Assert.Equal("Deleting", (await deletingRead.Content.ReadFromJsonAsync<ResourceConnectionDto>())!.Status);
    }

    [Fact]
    public async Task List_RejectsAnOffsetBeyondMongoSkipRange()
    {
        using var response = await Client.GetAsync(
            "/api/v1/resource-connections?pageNumber=2147483647&pageSize=100");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Get_ShowsRetiringCleanupFailureAlongsideCurrentTarget()
    {
        var original = ConnectionTargetSnapshot.Create(
            Id(),
            Id(),
            Id(),
            "old-namespace",
            "cache-svc",
            6379,
            6379,
            [new ConnectionSourcePlacement(Id(), Id(), "source-namespace")]);
        var connection = ResourceConnectionFactory.Create(
                Id(),
                TestData.TenantId,
                "moved-cache",
                SourceType,
                Id(),
                RedisConnectionEndpoints.ResourceType,
                Id(),
                "redis",
                [original])
            .RefreshResolvedTargets([original with
            {
                DestinationRuntimeCellId = Id(),
                DestinationNamespace = "new-namespace"
            }]);
        var failed = Assert.IsType<ConnectionCleanupRecorded>(
            connection.RecordCleanupFailure(
                original.PolicyLocation,
                connection.Generation,
                "TargetUnavailable",
                DateTime.UtcNow).Value).Connection;
        await Connections.InsertOneAsync(failed);

        using var response = await Client.GetAsync($"/api/v1/resource-connections/{failed.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var read = await response.Content.ReadFromJsonAsync<ResourceConnectionDto>();
        Assert.NotNull(read);
        Assert.Equal("Failed", read.Status);
        Assert.Equal(2, read.Targets.Count);
        Assert.Contains(read.Targets, target =>
            target.Phase == "Cleanup" && target.Status == "Failed" &&
            target.FailureCode == "TargetUnavailable" &&
            target.Host == "cache-svc.old-namespace.svc.cluster.local");
        Assert.Contains(read.Targets, target =>
            target.Phase == "Apply" && target.Status == "Pending" &&
            target.Host == "cache-svc.new-namespace.svc.cluster.local");
    }

    private IMongoCollection<ResourceConnection> Connections => Fixture.Database
        .GetCollection<ResourceConnection>("ResourceConnections");

    private async Task SetTenantRoleAsync(TenantRole role)
    {
        var tenant = await Fixture.DbContext.Tenants
            .Find(item => item.Id == TestData.TenantId)
            .SingleAsync();
        await Fixture.DbContext.Tenants.ReplaceOneAsync(
            item => item.Id == tenant.Id,
            tenant with
            {
                Members = ImmutableList.Create(TenantFactory.CreateMember(
                    TestData.TenantId,
                    TestData.UserId,
                    TestData.UserEmail,
                    role))
            });
    }

    private async Task<ResourceGroup> SeedReferenceAsync(
        ResourceTypeId resourceType,
        string resourceId,
        string groupName,
        string tenantId = TestData.TenantId)
    {
        var group = await Fixture.DbContext.InsertResourceGroupAsync(
            TestData.SeedResourceGroup(groupName, tenantId));
        await Fixture.DbContext.ResourceRegistrations.InsertOneAsync(
            ResourceRegistrationFactory.Create(
                tenantId,
                group.Id,
                resourceType,
                resourceId,
                groupName,
                owningPluginId: null,
                new ResourceRegistrationFactory.CreateOptions(Id(), DateTime.UtcNow)));
        if (resourceType == RedisConnectionEndpoints.ResourceType)
        {
            await Fixture.Database.GetCollection<Redis>("RedisServices").InsertOneAsync(
                RedisFactory.Create(
                    tenantId,
                    groupName,
                    group.Id,
                    new RedisSpec(new Storage(1, Id())),
                    RedisFactory.DefaultCreateOptions with { Id = resourceId }));
        }
        return group;
    }

    private async Task<ResourceDeployment> SeedDeploymentAsync(
        ResourceTypeId resourceType,
        string resourceId,
        ResourceGroup group,
        string targetId,
        string kubernetesNamespace)
    {
        var now = DateTime.UtcNow;
        var placement = ResolvedResourcePlacement.Create(
            Id(),
            targetId,
            kubernetesNamespace,
            runtimeCellGeneration: 1,
            targetWorkloadConfigurationRevision: 1,
            ingress: null,
            storage: null,
            ResourceDeploymentTestData.Compute(),
            now);
        var deployment = ResourceDeploymentFactory.Create(
            group.TenantId,
            resourceType,
            resourceId,
            group.Id,
            group.DefaultIsolationScopeId,
            placement,
            ResourcePlacementCapability.MultiTarget,
            runtimeCellReady: true,
            ResourceDeploymentFactory.DefaultOptions with
            {
                Id = Id(),
                CreatedAtUtc = now
            });
        await Fixture.DbContext.ResourceDeployments.InsertOneAsync(deployment);
        return deployment;
    }

    private static CreateResourceConnectionBody Body(
        string sourceId,
        string destinationId,
        string name = "orders-cache") => new(
        name,
        SourceType.Value,
        sourceId,
        RedisConnectionEndpoints.ResourceType.Value,
        destinationId,
        RedisConnectionEndpoints.Redis.Id);

    private static string Id() => ObjectId.GenerateNewId().ToString();
}
