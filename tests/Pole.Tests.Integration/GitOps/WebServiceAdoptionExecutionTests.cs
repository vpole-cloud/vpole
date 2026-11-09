using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.ComputeProfiles.Domain;
using Pole.Features.GitOps.Application;
using Pole.Features.GitOps.Application.Jobs;
using Pole.Features.GitOps.Application.Lifecycle;
using Pole.Features.GitOps.Application.Sources;
using Pole.Features.GitOps.Compiler;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Domain;
using Pole.Features.GitOps.Migrations;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application.Endpoints;
using Pole.Features.WebServices.Domain;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.GitOps;

[Collection(IntegrationTestCollection.Global2)]
public sealed class WebServiceAdoptionExecutionTests(MongoReplicaSetFixture fixture)
    : IsolatedMediatorIntegrationTestBase(fixture)
{
    private readonly TestRepositorySnapshots repository = new();
    private IMongoCollection<WebService> WebServices => Fixture.Database.GetCollection<WebService>("WebServices");
    private IMongoCollection<ManagedDeclaration> Inventory =>
        Fixture.Database.GetCollection<ManagedDeclaration>("ManagedDeclarations");

    protected override void ConfigureServices(IServiceCollection services)
    {
        NamedCatalogTestData.ConfigureIsolatedApi(services);
        services.AddSingleton<IRepositorySnapshotProvider>(repository);
    }

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await Fixture.PrepareNamedCatalogAsync();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("web-fence")]
    [InlineData("compute-policy")]
    public async Task Manual_web_adoption_validates_the_full_preview_vector_and_preserves_owner_data(string? change)
    {
        var group = await DbContext.InsertResourceGroupAsync(Data.SeedResourceGroup("demo"));
        var request = Data.CreateWebServiceRequest(group.Id, "manual-web");
        var created = (await Mediator.Send(request with
        {
            Body = request.Body with { Exposure = WebServiceExposureMode.Private }
        })).AssertSuccess<CreateWebServiceOutcome>();
        var id = Assert.IsType<CreateWebServiceResponse>(created.Value).WebServiceId;
        var original = await WebServices.Find(item => item.Id == id).SingleAsync();
        original = original with
        {
            Spec = original.Spec with
            {
                Image = Assert.IsType<ImageInfo>(original.Spec.Image) with { SecretName = "owner-pull-credential" }
            }
        };
        await WebServices.ReplaceOneAsync(item => item.Id == id, original);
        var defaults = TestData.ValidWebServiceSpec();
        var files = new[]
        {
            AdoptionDocument("ResourceGroup", group.Name, new { description = group.Description, tags = group.Tags }),
            AdoptionDocument("WebService", original.Name, new
            {
                resourceGroup = group.Name,
                placement = new
                {
                    deploymentTarget = NamedCatalogTestData.TargetName,
                    computeProfile = new { name = NamedCatalogTestData.ProfileName, revision = 1 }
                },
                image = new { server = "docker.io", name = "nginx", tag = "1.27" },
                exposure = "Private",
                replicas = 2,
                environment = defaults.Config.EnvironmentVariables,
                ports = defaults.Config.PortMappings,
                probes = defaults.Probes
            })
        };
        var source = await AdoptionSourceAsync(files);
        var previews = Services.GetRequiredService<GitLifecyclePreviews>();
        var preview = Assert.IsType<GitOperationPreview>((await previews.CreateAsync(new(
            new(source.TenantId, source.Id), source.Revision,
            new GitPreviewAdoption([new("ResourceGroup", group.Name, group.Id),
                new("WebService", original.Name, original.Id)])), default)).Value);
        var webAction = Assert.Single(preview.Operations, item => item.Resource.Kind == "WebService");
        Assert.Contains(webAction.Expected!.Versions, item => item.DocumentId == $"WebService/{id}");
        Assert.Contains(webAction.Expected.Versions, item => item.DocumentId == $"ResourceGroup/{group.Id}");
        Assert.Contains(webAction.Expected.Versions, item => item.DocumentId ==
            $"ComputeProfile/{TestData.DefaultComputeProfileId}/Policy");
        var scheduled = Assert.IsType<GitLifecycleScheduled>((await previews.ConfirmAsync(new(
            new(source.TenantId, source.Id), preview.Id, preview.Revision, Data.UserId), default)).Value);
        if (change == "web-fence")
            await WebServices.UpdateOneAsync(item => item.Id == id,
                Builders<WebService>.Update.Inc(item => item.PlanningFence, 1));
        if (change == "compute-policy")
            await Fixture.Database.GetCollection<ComputeProfile>("ComputeProfiles").UpdateOneAsync(
                item => item.Id == TestData.DefaultComputeProfileId,
                Builders<ComputeProfile>.Update.Inc(item => item.Revision, 1));
        var before = await WebServices.Find(item => item.Id == id).SingleAsync();
        await Services.GetRequiredService<RepositorySourceJob>().ProcessAsync(new(source.TenantId, source.Id), default);
        var run = (await Services.GetRequiredService<GitRepositorySources>()
            .RunAsync(source.TenantId, source.Id, scheduled.RunId, default))!;
        var completion = Assert.IsType<GitRunFinished>(run.State);
        var current = await WebServices.Find(item => item.Id == id).SingleAsync();
        if (change is not null)
        {
            Assert.Equal(GitRunCompletion.Blocked, completion.Completion);
            Assert.Contains(run.Diagnostics, item => item.Code == nameof(GitLifecycleApprovalStale));
            Assert.True(before.ToBson().SequenceEqual(current.ToBson()),
                "Rejected adoption must preserve the persisted owner document.");
            Assert.False(await Services.GetRequiredService<IMongoCollection<GitOperationReceipt>>()
                .Find(item => item.RunId == run.Id && item.Kind == "WebService").AnyAsync());
            Assert.False(await Inventory.Find(item => item.SourceId == source.Id && item.Kind == "WebService")
                .AnyAsync());
            return;
        }

        Assert.True(completion.Completion == GitRunCompletion.ConfigurationApplied,
            "Manual Web adoption must apply: " + string.Join(",", run.Diagnostics.Select(item => item.Code)));
        Assert.Equal(original.Id, current.Id);
        Assert.Equal(original.Revision, current.Revision);
        Assert.Equal(original.ActiveRevision, current.ActiveRevision);
        Assert.True(original.Spec.HasSameConfiguration(current.Spec));
        Assert.Equal("owner-pull-credential", Assert.IsType<ImageInfo>(current.Spec.Image).SecretName);
        Assert.Equal(NamedCatalogTestData.TargetName, current.DeclaredPlacement!.DeploymentTargetName);
        Assert.Equal(NamedCatalogTestData.ProfileName, current.DeclaredPlacement.ComputeProfileName);
        Assert.Equal(TestData.DefaultDeploymentTargetId, current.DeclaredPlacement.DeploymentTargetId);
        Assert.Equal(TestData.DefaultComputeProfileId, current.DeclaredPlacement.ComputeProfileId);
        Assert.Equal(2, await Inventory.CountDocumentsAsync(item => item.SourceId == source.Id));
        var receipts = await Services.GetRequiredService<IMongoCollection<GitOperationReceipt>>()
            .Find(item => item.RunId == run.Id && item.ResourceId == id).ToListAsync();
        Assert.Single(receipts, item => item.Lifecycle?.Step == GitLifecycleStep.Adopt);
        Assert.Contains(receipts, item => item.Lifecycle?.Step == GitLifecycleStep.Apply);
    }

    private async Task<RepositorySource> AdoptionSourceAsync(ManifestFile[] files)
    {
        var repositoryId = ObjectId.GenerateNewId().ToString();
        repository.Publish(repositoryId, files);
        return Assert.IsType<RepositorySource>((await Services.GetRequiredService<GitRepositorySources>().CreateAsync(
            new(Data.TenantId, Guid.NewGuid().ToString("D"),
                new(repository.Provider, TestRepositorySnapshots.ConnectionId, repositoryId, "main", ""),
                new(TimeSpan.FromMinutes(5)), new(Data.UserId, DateTime.UtcNow),
                TestRepositorySnapshots.DisplayUrl), default)).Value);
    }

    private static ManifestFile AdoptionDocument(string kind, string name, object spec) => new(
        kind + ".yaml", JsonSerializer.Serialize(new
        {
            apiVersion = "pole.io/v1alpha1",
            kind,
            metadata = new { name },
            spec
        }, DeclarationJson.Options));
}
