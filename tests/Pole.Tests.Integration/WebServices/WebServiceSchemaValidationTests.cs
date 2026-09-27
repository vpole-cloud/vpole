using System.Collections.Immutable;
using MongoDB.Bson;
using Pole.Features.Shared.Models;
using Pole.Features.WebServices.Application.Migrations;
using Pole.Features.WebServices.Domain;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.WebServices;

[Collection(IntegrationTestCollection.Name)]
public sealed class WebServiceSchemaValidationTests(MongoReplicaSetFixture fixture) : IAsyncLifetime
{
    private const string CollectionName = "WebServices";
    private const string ReleaseCollectionName = "WebServiceReleases";

    public Task InitializeAsync() => fixture.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Migration_creates_collection_that_accepts_current_document_shapes()
    {
        await new WebServiceSchemaValidationMigration().Up(fixture.Database);
        var contract = CreateContract();

        await contract.AssertStrictErrorValidationAsync();
        await contract.AssertAcceptsAsync(
        [
            CreateWebService(new ActiveWebServiceLifecycle()),
            CreateWebService(
                new ActiveWebServiceLifecycle(),
                secretName: "registry-secret",
                autoscaling: new AutoscalingSpec(
                    1,
                    5,
                    new AutoscalingMetric("cpu", 70)),
                withConfiguration: true),
            CreateWebService(new DeletingWebServiceLifecycle(DateTime.UtcNow))
        ]);
    }

    [Fact]
    public async Task Migration_updates_existing_collection_and_rejects_missing_tenant_scope()
    {
        await fixture.Database.CreateCollectionAsync(CollectionName);
        await new WebServiceSchemaValidationMigration().Up(fixture.Database);
        var contract = CreateContract();

        await contract.AssertStrictErrorValidationAsync();
        await contract.AssertRejectsAsync(
            CreateWebService(new ActiveWebServiceLifecycle()),
            document => document.Remove(nameof(WebService.TenantId)));
    }

    [Fact]
    public async Task Validator_rejects_deleting_lifecycle_without_requested_timestamp()
    {
        await new WebServiceSchemaValidationMigration().Up(fixture.Database);

        await CreateContract().AssertRejectsAsync(
            CreateWebService(new DeletingWebServiceLifecycle(DateTime.UtcNow)),
            document => document[nameof(WebService.Lifecycle)].AsBsonDocument.Remove(
                nameof(DeletingWebServiceLifecycle.RequestedAtUtc)));
    }

    [Fact]
    public async Task Validator_rejects_unknown_lifecycle_discriminator()
    {
        await new WebServiceSchemaValidationMigration().Up(fixture.Database);

        await CreateContract().AssertRejectsAsync(
            CreateWebService(new ActiveWebServiceLifecycle()),
            document => document[nameof(WebService.Lifecycle)].AsBsonDocument["_t"] =
                new BsonArray { "WebServiceLifecycle", "UnknownWebServiceLifecycle" });
    }

    [Fact]
    public async Task Validator_requires_a_known_exposure_mode()
    {
        await new WebServiceSchemaValidationMigration().Up(fixture.Database);
        var contract = CreateContract();

        await contract.AssertRejectsAsync(
            CreateWebService(new ActiveWebServiceLifecycle()),
            document => document[nameof(WebService.Exposure)].AsBsonDocument.Remove("Mode"));
        await contract.AssertRejectsAsync(
            CreateWebService(new ActiveWebServiceLifecycle()),
            document => document[nameof(WebService.Exposure)].AsBsonDocument["Mode"] = "Internal");
    }

    [Fact]
    public async Task ReleaseMigration_accepts_retry_deployment_action()
    {
        await new WebServiceReleaseSchemaValidationMigration().Up(fixture.Database);
        var deployment = new WebServiceReleaseDeployment(
            ObjectId.GenerateNewId().ToString(),
            ObjectId.GenerateNewId().ToString(),
            ResourceRevision: 2,
            DeploymentGeneration: 1,
            WebServiceReleaseDeploymentOperation.Apply,
            WebServiceReleaseDeploymentOutcome.Pending,
            CompletedAtUtc: null,
            FailureCode: null,
            FailureMessage: null);
        var release = WebServiceReleaseFactory.CreatePending(
            TestData.TenantId,
            ObjectId.GenerateNewId().ToString(),
            2,
            WebServiceReleaseAction.RetryDeployment,
            null,
            null,
            CreateWebService(new ActiveWebServiceLifecycle()).Spec,
            [deployment],
            null,
            null,
            new WebServiceReleaseFactory.CreateOptions(
                ObjectId.GenerateNewId().ToString(),
                DateTime.UtcNow),
            WebServiceExposure.Create(WebServiceExposureMode.Public));

        await new MongoDocumentContractTester<WebServiceRelease>(
                fixture.Database,
                ReleaseCollectionName)
            .AssertAcceptsAsync([release]);
    }

    private MongoDocumentContractTester<WebService> CreateContract() =>
        new(fixture.Database, CollectionName);

    private static WebService CreateWebService(
        WebServiceLifecycle lifecycle,
        string? secretName = null,
        AutoscalingSpec? autoscaling = null,
        bool withConfiguration = false) =>
        WebServiceFactory.Create(
            "api",
            TestData.TenantId,
            ObjectId.GenerateNewId().ToString(),
            ObjectId.GenerateNewId().ToString(),
            new WebServiceSpec(
                new ImageInfo("registry.example.com", "team/api", "latest", secretName),
                new WebServiceConfig(
                    withConfiguration
                        ? ImmutableDictionary<string, string>.Empty.Add("ASPNETCORE_ENVIRONMENT", "Development")
                        : ImmutableDictionary<string, string>.Empty,
                    withConfiguration
                        ? ImmutableList.Create(new PortMapping(80, "8080", "TCP", "http"))
                        : ImmutableList<PortMapping>.Empty),
                autoscaling,
                CreateProbes()),
            WebServiceExposure.Create(generatedHostnameLabelPrefix: "api"),
            WebServiceFactory.DefaultOptions with
            {
                Id = ObjectId.GenerateNewId().ToString(),
                Tags = withConfiguration
                    ? ImmutableHashSet.Create("development")
                    : ImmutableHashSet<string>.Empty,
                Events = withConfiguration
                    ? ImmutableList.Create(new ServiceEvent(DateTime.UtcNow, "created"))
                    : ImmutableList<ServiceEvent>.Empty,
                Lifecycle = lifecycle
            });

    private static ProbeSpec CreateProbes()
    {
        var probe = new ProbeConfig("/health", "8080");
        return new ProbeSpec(probe, probe, probe);
    }
}
