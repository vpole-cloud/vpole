using MongoDB.Bson;
using MongoDB.Driver;
using Microsoft.Extensions.Logging.Abstractions;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application.Consumers;
using Pole.Features.WebServices.Application.Kubernetes;
using Pole.Features.WebServices.Contracts;
using Pole.Features.WebServices.Domain;
using Pole.Features.WebServices.Events;
using Pole.ResultType;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.WebServices;

public sealed class WebServiceSourceBuildDeploymentTests(MongoReplicaSetFixture fixture)
    : ApiIntegrationTestBase(fixture)
{
    [Fact]
    public async Task SourceBuildDeployment_CreatesTypedProvenanceAndCompletionEvent()
    {
        var webService = TestData.SeedWebService(ObjectId.GenerateNewId().ToString());
        await Fixture.DbContext.WebServices.InsertOneAsync(webService);
        var service = new WebServiceSourceBuildDeploymentService(Fixture.DbContext);
        var request = new SourceBuildDeploymentRequest(
            TestData.TenantId,
            webService.Id,
            "build-1",
            "source-1",
            "abc123",
            TestData.UserId,
            TestData.UserEmail,
            new SourceBuildDeploymentImage(
                "harbor.test",
                "pole-test/web-services/service-1",
                "build-1",
                "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                "pole-registry-project-1"));

        var result = await service.DeployAsync(request);

        Assert.True(result.TryGetValue(out var deployment, out _));
        var updated = await Fixture.DbContext.WebServices.Find(item => item.Id == webService.Id).SingleAsync();
        Assert.Equal("harbor.test", updated.Spec.Image.Server);
        Assert.Equal("pole-registry-project-1", updated.Spec.Image.SecretName);
        var release = await Fixture.DbContext.WebServiceReleases
            .Find(item => item.Id == deployment.ReleaseId)
            .SingleAsync();
        Assert.Equal(WebServiceReleaseAction.SourceBuild, release.Action);
        var provenance = Assert.IsType<SourceBuildReleaseProvenance>(release.Provenance);
        Assert.Equal("build-1", provenance.BuildJobId);
        Assert.Equal(request.Image.Digest, provenance.ImageDigest);

        var requested = new WebServiceDeploymentRequestedEvent(
            release.Id,
            release.Revision,
            release.Action.ToString(),
            updated.Id,
            updated.Name,
            updated.ResourceGroupId,
            updated.DnsRecord,
            release.Spec)
        {
            EventCore = Pole.Features.Events.Abstractions.EventCore.Create(TestData.TenantId)
        };
        var consumer = new ApplyWebServiceDeploymentOnWebServiceDeploymentRequestedConsumer(
            Fixture.DbContext,
            new SuccessfulDeploymentApplier(),
            NullLogger<ApplyWebServiceDeploymentOnWebServiceDeploymentRequestedConsumer>.Instance);
        await consumer.Handle(requested);

        var completedRelease = await Fixture.DbContext.WebServiceReleases
            .Find(item => item.Id == release.Id)
            .SingleAsync();
        Assert.Equal(WebServiceReleaseOutcome.Succeeded, completedRelease.Outcome);
        Assert.True(await Fixture.DbContext.OutboxMessages
            .Find(item => item.EventType == "web-service-deployment-completed")
            .AnyAsync());
    }

    private sealed class SuccessfulDeploymentApplier : IWebServiceDeploymentApplier
    {
        public Task<Result> ApplyAsync(
            WebServiceDeploymentRequestedEvent deploymentEvent,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<Result>(Result.Success());
    }
}
