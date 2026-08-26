using System.Collections.Immutable;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.Events.Abstractions;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.ResourceGroups.Application.Endpoints;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.Request;
using Pole.Features.Search.Application.Common;
using Pole.Features.Search.Application.Consumers;
using Pole.Features.Search.Domain;
using Pole.Features.Search.Application.Projection;
using Pole.Features.Search.Application.Persistence;
using Pole.Features.Shared.Models;
using Pole.Features.WebServices.Application.Endpoints;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Domain;
using Pole.Features.WebServices.Events;
using Pole.Mediator;
using Pole.Features.RedisServices.Domain;
using Pole.Features.RedisServices.Events;
using Pole.Features.Postgres.Domain;
using Pole.Features.Postgres.Events;
using Pole.Tests.Integration.Infrastructure;
using Pole.Tests.Integration.Postgres;
using Xunit;
using ResourceGroupCreateEvent = Pole.Features.ResourceGroups.Events.ResourceGroupCreatedEvent;
using ResourceGroupUpdateEvent = Pole.Features.ResourceGroups.Events.ResourceGroupUpdatedEvent;
using ResourceGroupUpdateEventChanges = Pole.Features.ResourceGroups.Events.ResourceGroupUpdatedEventChanges;
using WebServiceCreateEvent = Pole.Features.WebServices.Events.WebServiceCreatedEvent;
using RedisCreateEvent = Pole.Features.RedisServices.Events.RedisServiceCreatedEvent;
using PostgresCreateEvent = Pole.Features.Postgres.Events.PostgresClusterCreatedEvent;

namespace Pole.Tests.Integration.Search;

public sealed class SearchIntegrationTests(MongoReplicaSetFixture fixture)
    : MediatorIntegrationTestBase(fixture)
{
    private PersistSearchDocumentSet PersistSearchDocumentSet => Services.GetRequiredService<PersistSearchDocumentSet>();
    private RemoveSearchDocumentSet RemoveSearchDocumentSet => Services.GetRequiredService<RemoveSearchDocumentSet>();
    private SearchIndexedDocuments SearchIndexedDocuments => Services.GetRequiredService<SearchIndexedDocuments>();
    private IMongoCollection<SearchDocument> SearchDocuments => Fixture.Database.GetCollection<SearchDocument>("SearchDocuments");
    private IMongoCollection<SearchSourceDocument> SearchSourceDocuments => Fixture.Database.GetCollection<SearchSourceDocument>("SearchSourceDocuments");

    [Fact]
    public async Task Create_ProducesMultipleRows_WithFullValuesAndTokens()
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup("alpha-shared"));

        await IndexResourceGroupCreatedAsync(resourceGroup.Id);

        var createResult = await Mediator.Send(TestData.CreateWebServiceRequest(resourceGroup.Id));
        var created = createResult.AssertSuccess();

        await IndexWebServiceCreatedAsync(created.WebServiceId);

        var rows = await SearchDocuments.Find(row => row.SourceType == SearchDocumentType.WebService && row.SourceId == created.WebServiceId).ToListAsync();
        var sourceDocument = await SearchSourceDocuments.Find(row => row.SourceType == SearchDocumentType.WebService && row.SourceId == created.WebServiceId).FirstOrDefaultAsync();

        Assert.Contains(rows, row => row.KeyType == SearchKeyType.Name && row is { SearchValue: "web-api", Weight: SearchDocumentFactory.NameWeight });
        Assert.Contains(rows, row => row.KeyType == SearchKeyType.Name && row is { SearchValue: "web", Weight: SearchDocumentFactory.NameTokenWeight });
        Assert.Contains(rows, row => row.KeyType == SearchKeyType.Name && row is { SearchValue: "api", Weight: SearchDocumentFactory.NameTokenWeight });
        var generatedHostname = created.Deployments.Single().Ingress!.Hostnames.Single();
        Assert.Contains(rows, row => row.KeyType == SearchKeyType.Domain && row is { SearchValue: var value, Weight: SearchDocumentFactory.DomainWeight } && value == generatedHostname);
        Assert.DoesNotContain(rows, row => row.SearchValue == "description");
        Assert.NotNull(sourceDocument);
        Assert.Equal("web-api", sourceDocument.Title);
        Assert.Contains(sourceDocument.SearchKeys, key => key.Value == "web-api" && key.Weight == SearchDocumentFactory.NameWeight);
        Assert.Contains(sourceDocument.SearchKeys, key => key.Value == "api" && key.Weight == SearchDocumentFactory.NameTokenWeight);
        Assert.Contains(sourceDocument.SearchKeys, key => key.Value == generatedHostname && key.Weight == SearchDocumentFactory.DomainWeight);
    }

    [Fact]
    public async Task Get_ReturnsGroupedResults_AfterPrefixMatching()
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup("alpha-shared"));

        await IndexResourceGroupCreatedAsync(resourceGroup.Id);

        var webCreate = await Mediator.Send(TestData.CreateWebServiceRequest(resourceGroup.Id, "alpha-web"));
        var webResponse = webCreate.AssertSuccess();
        await IndexWebServiceCreatedAsync(webResponse.WebServiceId);

        var redisCreate = await Mediator.Send(TestData.CreateRedisServiceRequest(resourceGroup.Id, "alpha-redis"));
        var redisResponse = redisCreate.AssertSuccess();
        await IndexRedisCreatedAsync(redisResponse.RedisId);

        var postgresCreate = await Mediator.Send(TestData.CreatePostgresClusterRequest(resourceGroup.Id, "alpha-postgres"));
        var postgresResponse = PostgresOutcomeAssertions.AssertCreated(postgresCreate);
        await IndexPostgresCreatedAsync(postgresResponse.Id);

        var result = await Mediator.Send(new GetSearchRequest
        {
            TenantId = TestData.TenantId,
            Query = "alp",
            PageNumber = 1,
            PageSize = 20
        });

        var response = AssertSearchPage(result);
        Assert.Contains(response.Items, item => item.SourceType == "ResourceGroup");
        Assert.Contains(response.Items, item => item.SourceType == "WebService");
        Assert.Contains(response.Items, item => item.SourceType == "RedisService");
        Assert.Contains(response.Items, item => item.SourceType == "PostgresCluster");
        Assert.Equal(response.Items.Count(), response.Items.Select(item => $"{item.SourceType}:{item.SourceId}").Distinct().Count());

        var filtered = await Mediator.Send(new GetSearchRequest
        {
            TenantId = TestData.TenantId,
            Query = "alp",
            PageNumber = 1,
            PageSize = 20,
            Types = ["WebService"]
        });

        var filteredResponse = AssertSearchPage(filtered);
        Assert.Single(filteredResponse.Items);
        Assert.Equal("WebService", filteredResponse.Items.Single().SourceType);

        var serviceQuery = SearchQueryModule.FromRequest(new GetSearchRequest
        {
            TenantId = TestData.TenantId,
            Query = "alp",
            PageNumber = 1,
            PageSize = 20
        });
        var serviceResult = await SearchIndexedDocuments.Invoke(serviceQuery, CancellationToken.None);

        Assert.Equal(response.TotalCount, serviceResult.TotalCount);
        Assert.Equal(
            response.Items.Select(item => $"{item.SourceType}:{item.SourceId}"),
            serviceResult.Items.Select(item => $"{item.SourceType}:{item.SourceId}"));
    }

    [Fact]
    public async Task IdMatch_Outranks_NameMatch()
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup("alpha-shared"));

        var webCreate = await Mediator.Send(TestData.CreateWebServiceRequest(resourceGroup.Id, "service-123"));
        var created = webCreate.AssertSuccess();
        await IndexWebServiceCreatedAsync(created.WebServiceId);

        var secondResourceGroup = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup("beta-shared"));
        var otherCreate = await Mediator.Send(TestData.CreateWebServiceRequest(secondResourceGroup.Id, created.WebServiceId[..8]));
        var otherCreated = otherCreate.AssertSuccess();
        await IndexWebServiceCreatedAsync(otherCreated.WebServiceId);

        var result = await Mediator.Send(new GetSearchRequest
        {
            TenantId = TestData.TenantId,
            Query = created.WebServiceId[..8].ToLowerInvariant(),
            PageNumber = 1,
            PageSize = 10
        });

        var response = AssertSearchPage(result);
        Assert.Equal(created.WebServiceId, response.Items.First().SourceId);
    }

    [Fact]
    public async Task Get_IsTenantScoped()
    {
        var tenantId = ObjectId.GenerateNewId().ToString();
        var otherTenantId = ObjectId.GenerateNewId().ToString();
        var sourceId = ObjectId.GenerateNewId().ToString();
        var otherSourceId = ObjectId.GenerateNewId().ToString();

        await PersistSearchDocumentSet.Invoke(
            CreateSearchDocumentSet(tenantId, sourceId, "shared-service"),
            CancellationToken.None);
        await PersistSearchDocumentSet.Invoke(
            CreateSearchDocumentSet(otherTenantId, otherSourceId, "shared-service"),
            CancellationToken.None);

        var result = await Mediator.Send(new GetSearchRequest
        {
            TenantId = tenantId,
            Query = "shared",
            PageNumber = 1,
            PageSize = 10
        });

        var hit = Assert.Single(AssertSearchPage(result).Items);
        Assert.Equal(sourceId, hit.SourceId);
    }

    [Fact]
    public async Task ResourceGroupUpdate_RebuildsProjection_WithoutDescriptionRows()
    {
        var createResult = await Mediator.Send(TestData.CreateResourceGroupRequest("ops"));
        var created = createResult.AssertHandlerOutcome() switch
        {
            ResourceGroupDto success => success,
            CreateResourceGroupError creationError => throw new Xunit.Sdk.XunitException(
                $"Expected resource-group creation success, got {creationError.GetType().Name}.")
        };

        await IndexResourceGroupCreatedAsync(created.Id);

        var updateResult = await Mediator.Send(new UpdateResourceGroupRequest(
            created.Id,
            new UpdateResourceGroupRequestBody(
                Description: "Updated for search",
                Tags: ["team-blue", "prod"]),
            TestData.TenantId));

        _ = updateResult.AssertHandlerOutcome();

        await IndexResourceGroupUpdatedAsync(created.Id);

        var rows = await SearchDocuments.Find(row => row.SourceType == SearchDocumentType.ResourceGroup && row.SourceId == created.Id).ToListAsync();
        var sourceDocument = await SearchSourceDocuments.Find(row => row.SourceType == SearchDocumentType.ResourceGroup && row.SourceId == created.Id).FirstOrDefaultAsync();
        Assert.Contains(rows, row => row.KeyType == SearchKeyType.Tag && row.SearchValue == "team-blue");
        Assert.DoesNotContain(rows, row => row.SearchValue == "updated");
        Assert.NotNull(sourceDocument);
        Assert.Contains(sourceDocument.SearchKeys, key => key.Value == "team-blue" && key.Weight == SearchDocumentFactory.TagWeight);
        Assert.DoesNotContain(sourceDocument.SearchKeys, key => key.Value == "updated");

        var searchResult = await Mediator.Send(new GetSearchRequest
        {
            TenantId = TestData.TenantId,
            Query = "team",
            PageNumber = 1,
            PageSize = 10
        });

        var response = AssertSearchPage(searchResult);
        Assert.Single(response.Items);
        Assert.Equal(created.Id, response.Items.Single().SourceId);
    }

    [Fact]
    public async Task WebServiceDelete_RemovesAllRows()
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup("apps"));
        var createResult = await Mediator.Send(TestData.CreateWebServiceRequest(resourceGroup.Id, "portal"));
        var created = createResult.AssertSuccess();

        await IndexWebServiceCreatedAsync(created.WebServiceId);

        var deleteResult = await Mediator.Send(new DeleteWebServiceRequest(created.WebServiceId, TestData.TenantId));
        _ = deleteResult.AssertHandlerOutcome();

        var deletedConsumer = new RemoveWebServiceFromSearchOnWebServiceDeletedConsumer(RemoveSearchDocumentSet);
        await deletedConsumer.Handle(TestData.TenantId, created.WebServiceId, CancellationToken.None);

        var serviceRows = await SearchDocuments.CountDocumentsAsync(row => row.SourceType == SearchDocumentType.WebService && row.SourceId == created.WebServiceId);
        var serviceSourceRows = await SearchSourceDocuments.CountDocumentsAsync(row => row.SourceType == SearchDocumentType.WebService && row.SourceId == created.WebServiceId);

        Assert.Equal(0L, serviceRows);
        Assert.Equal(0L, serviceSourceRows);
    }

    private async Task IndexResourceGroupCreatedAsync(string resourceGroupId)
    {
        var resourceGroup = await DbContext.ResourceGroups.Find(group => group.Id == resourceGroupId).FirstAsync();
        var consumer = new AddResourceGroupToSearchOnResourceGroupCreatedConsumer(PersistSearchDocumentSet);
        await consumer.Handle(new ResourceGroupCreateEvent(
            resourceGroup.Id,
            resourceGroup.Name,
            resourceGroup.Description,
            resourceGroup.KubernetesNamespace,
            resourceGroup.Resources,
            resourceGroup.Tags)
        {
            EventCore = EventCore.Create(resourceGroup.TenantId)
        }, CancellationToken.None);
    }

    private async Task IndexResourceGroupUpdatedAsync(string resourceGroupId)
    {
        var resourceGroup = await DbContext.ResourceGroups.Find(group => group.Id == resourceGroupId).FirstAsync();
        var consumer = new UpdateResourceGroupInSearchOnResourceGroupUpdatedConsumer(PersistSearchDocumentSet);
        await consumer.Handle(new ResourceGroupUpdateEvent(
            resourceGroup.Id,
            resourceGroup.Name,
            resourceGroup.Description,
            resourceGroup.KubernetesNamespace,
                resourceGroup.Resources,
                new ResourceGroupUpdateEventChanges(
                    UpdateLog<string>.Of("", resourceGroup.Description),
                    UpdateLog<ImmutableHashSet<string>>.Of([], resourceGroup.Tags)))
        {
            EventCore = EventCore.Create(resourceGroup.TenantId)
        }, CancellationToken.None);
    }

    private async Task IndexWebServiceCreatedAsync(string webServiceId)
    {
        var webService = await Fixture.Database.WebServices().Find(service => service.Id == webServiceId).FirstAsync();
        var placements = await Services.GetRequiredService<LoadResourceDeploymentSet>()(
            webService.TenantId, WebServiceResourceDeployment.ResourceType, webService.Id);
        var consumer = new AddWebServiceToSearchOnWebServiceCreatedConsumer(PersistSearchDocumentSet);
        await consumer.Handle(new WebServiceCreateEvent(
            webService.Id,
            webService.Name,
            webService.ResourceGroupId,
            webService.IsolationScopeId,
            webService.ActiveRevision,
            ToSnapshot(webService.Spec),
            new WebServiceExposureSnapshot(webService.Exposure.GeneratedHostnameLabelPrefix),
            webService.Tags,
            placements.Select(placement => new WebServiceDesiredDeploymentSnapshot(
                    placement.DeploymentId,
                    placement.DeploymentTargetId,
                    placement.ResourceRevision,
                    placement.DesiredGeneration,
                    "Apply",
                    placement.Ingress?.Hostnames ?? ImmutableList<string>.Empty))
                .ToImmutableList())
        {
            EventCore = EventCore.Create(webService.TenantId)
        }, CancellationToken.None);
    }

    private async Task IndexRedisCreatedAsync(string redisId)
    {
        var redis = await DbContext.Database.RedisServices().Find(service => service.Id == redisId).FirstAsync();
        var consumer = new AddRedisServiceToSearchOnRedisServiceCreatedConsumer(PersistSearchDocumentSet);
        await consumer.Handle(new RedisCreateEvent(
            redis.Id,
            redis.Name,
            redis.ResourceGroupId,
            ToSnapshot(redis.Spec),
            redis.Tags)
        {
            EventCore = EventCore.Create(redis.TenantId)
        }, CancellationToken.None);
    }

    private async Task IndexPostgresCreatedAsync(string clusterId)
    {
        var cluster = await DbContext.Postgres.Find(cluster => cluster.Id == clusterId).FirstAsync();
        var consumer = new AddPostgresClusterToSearchOnPostgresClusterCreatedConsumer(PersistSearchDocumentSet);
        await consumer.Handle(new PostgresCreateEvent(
            EventCore.Create(cluster.TenantId),
            cluster.Id,
            cluster.Name,
            cluster.GroupId,
            ToSnapshot(cluster.Spec)), CancellationToken.None);
    }

    private static PaginatedResponse<SearchHitDto> AssertSearchPage(MediatorResult<GetSearchOutcome> result) =>
        result.AssertHandlerOutcome().Item;

    private static SearchDocumentSet CreateSearchDocumentSet(
        string tenantId,
        string sourceId,
        string title) =>
        new(
            SearchDocumentType.WebService,
            tenantId,
            sourceId,
            ListModule.OfSeq(
            [
                new SearchDocument(
                    ObjectId.GenerateNewId().ToString(),
                    SearchDocumentType.WebService,
                    tenantId,
                    sourceId,
                    SearchKeyType.Name,
                    title,
                    SearchDocumentFactory.NameWeight,
                    title,
                    FSharpOption<string>.None,
                    FSharpOption<string>.None)
            ]));

    private static WebServiceSpecSnapshot ToSnapshot(WebServiceSpec spec) =>
        new(
            new WebServiceImageInfoSnapshot(
                spec.Image.Server,
                spec.Image.Name,
                spec.Image.Tag,
                spec.Image.SecretName),
            new WebServiceConfigSnapshot(
                spec.Config.EnvironmentVariables,
                spec.Config.PortMappings
                    .Select(mapping => new PortMappingSnapshot(
                        mapping.ServicePort,
                        mapping.TargetPort,
                        mapping.Protocol,
                        mapping.Name))
                    .ToImmutableList()),
            spec.Autoscaling is null
                ? null
                : new AutoscalingSpecSnapshot(
                    spec.Autoscaling.MinReplicas,
                    spec.Autoscaling.MaxReplicas,
                    new AutoscalingMetricSnapshot(
                        spec.Autoscaling.Metric.Type,
                        spec.Autoscaling.Metric.TargetAverageUtilization)),
            new WebServiceProbeSpecSnapshot(
                ToSnapshot(spec.Probes.Startup),
                ToSnapshot(spec.Probes.Liveness),
                ToSnapshot(spec.Probes.Readiness)));

    private static WebServiceProbeConfigSnapshot ToSnapshot(ProbeConfig probe) =>
        new(
            probe.Path,
            probe.Port,
            probe.InitialDelaySeconds,
            probe.PeriodSeconds,
            probe.TimeoutSeconds,
            probe.FailureThreshold,
            probe.SuccessThreshold);

    private static RedisServiceSpecSnapshot ToSnapshot(RedisSpec spec) =>
        new(
            new RedisServiceStorageSnapshot(
                spec.Storage.SizeGb,
                spec.Storage.StorageProfileId));

    private static PostgresClusterSpecSnapshot ToSnapshot(PostgresClusterSpec spec) =>
        new(
            spec.Instances,
            spec.Storage,
            spec.StorageProfileId,
            spec.Version,
            spec.DeploymentMode switch
            {
                PostgresDeploymentMode.StatefulSet => PostgresDeploymentModeSnapshot.StatefulSet,
                _ => PostgresDeploymentModeSnapshot.Operator
            });
}
