using MongoDB.Driver;
using Microsoft.Extensions.DependencyInjection;
using Pole.Features.Events.Abstractions;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Events;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application.Consumers;
using Pole.Features.WebServices.Application.Persistence;
using Pole.Features.WebServices.Application.Persistence.Deployments;
using Pole.Features.WebServices.Domain;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.WebServices;

public sealed class WebServiceDeploymentConsumerTests(MongoReplicaSetFixture fixture)
    : MediatorIntegrationTestBase(fixture)
{
    [Fact]
    public async Task Handle_MarksReleaseSucceeded_WhenDeploymentApplySucceeds()
    {
        var release = await InsertPendingReleaseAsync();
        var consumer = new WebServiceDeploymentConsumer(
            Services.GetRequiredService<CompleteWebServiceDeployment>(),
            TimeProvider.System);

        await consumer.Handle(CreateDeploymentEvent(release));

        var saved = await Fixture.Database.WebServiceReleases()
            .Find(r => r.Id == release.Id)
            .SingleAsync();
        Assert.Equal(WebServiceReleaseOutcome.Succeeded, saved.Outcome);
        Assert.NotNull(saved.CompletedAtUtc);
        Assert.Null(saved.FailureMessage);
    }

    [Fact]
    public async Task Handle_LeavesReleasePending_WhenAppliedGenerationDoesNotMatch()
    {
        var release = await InsertPendingReleaseAsync();
        var consumer = new WebServiceDeploymentConsumer(
            Services.GetRequiredService<CompleteWebServiceDeployment>(),
            TimeProvider.System);
        var applied = CreateDeploymentEvent(release) with
        {
            Generation = release.Deployments[0].DeploymentGeneration + 1
        };

        await consumer.Handle(applied);

        var saved = await Fixture.Database.WebServiceReleases()
            .Find(r => r.Id == release.Id)
            .SingleAsync();
        Assert.Equal(WebServiceReleaseOutcome.Pending, saved.Outcome);
        Assert.Null(saved.CompletedAtUtc);
        Assert.Equal(
            0L,
            await DbContext.CountOutboxMessagesAsync(
                "pole.web-services/v1/web-service-deployment-completed"));
    }

    [Fact]
    public async Task Handle_DoesNotPublishAnotherCompletion_WhenDeliveryIsRepeated()
    {
        var release = await InsertPendingReleaseAsync();
        var consumer = new WebServiceDeploymentConsumer(
            Services.GetRequiredService<CompleteWebServiceDeployment>(),
            TimeProvider.System);
        var deployment = CreateDeploymentEvent(release);

        await consumer.Handle(deployment);
        await consumer.Handle(deployment);

        var completionEvents = await DbContext.OutboxMessages
            .Find(message => message.EventType == "pole.web-services/v1/web-service-deployment-completed")
            .ToListAsync();
        Assert.Single(completionEvents);
    }

    [Fact]
    public async Task Handle_MarksReleaseFailed_WhenDeploymentFailureIsTerminal()
    {
        var release = await InsertPendingReleaseAsync();
        var consumer = new WebServiceDeploymentConsumer(
            Services.GetRequiredService<CompleteWebServiceDeployment>(),
            TimeProvider.System);

        await consumer.Handle(CreateFailureEvent(release, retryable: false));

        var saved = await Fixture.Database.WebServiceReleases()
            .Find(item => item.Id == release.Id)
            .SingleAsync();
        Assert.Equal(WebServiceReleaseOutcome.Failed, saved.Outcome);
        Assert.NotNull(saved.CompletedAtUtc);
        Assert.Contains(
            "ManifestInvalid",
            saved.FailureMessage,
            StringComparison.Ordinal);
        Assert.Equal(
            1L,
            await DbContext.CountOutboxMessagesAsync(
                "pole.web-services/v1/web-service-deployment-completed"));
    }

    [Fact]
    public async Task Handle_LeavesReleasePending_WhenDeploymentFailureIsRetryable()
    {
        var release = await InsertPendingReleaseAsync();
        var consumer = new WebServiceDeploymentConsumer(
            Services.GetRequiredService<CompleteWebServiceDeployment>(),
            TimeProvider.System);

        await consumer.Handle(CreateFailureEvent(release, retryable: true));

        var saved = await Fixture.Database.WebServiceReleases()
            .Find(item => item.Id == release.Id)
            .SingleAsync();
        Assert.Equal(WebServiceReleaseOutcome.Pending, saved.Outcome);
        Assert.Null(saved.CompletedAtUtc);
        Assert.Equal(
            0L,
            await DbContext.CountOutboxMessagesAsync(
                "pole.web-services/v1/web-service-deployment-completed"));
    }

    private async Task<WebServiceRelease> InsertPendingReleaseAsync()
    {
        var release = WebServiceReleaseFactory.CreatePending(
            TestData.TenantId,
            "507f1f77bcf86cd799439041",
            2,
            WebServiceReleaseAction.Redeploy,
            TestData.UserId,
            TestData.UserEmail,
            TestData.ValidWebServiceSpec().ToDomain(),
            [new WebServiceReleaseDeployment(
                "507f1f77bcf86cd799439051",
                "507f1f77bcf86cd799439052",
                ResourceRevision: 2,
                DeploymentGeneration: 3,
                WebServiceReleaseDeploymentOperation.Apply,
                WebServiceReleaseDeploymentOutcome.Pending,
                CompletedAtUtc: null,
                FailureCode: null,
                FailureMessage: null)],
            "trace-1",
            "correlation-1",
            new WebServiceReleaseFactory.CreateOptions(
                MongoDB.Bson.ObjectId.GenerateNewId().ToString(),
                DateTime.UtcNow),
            WebServiceExposure.Create(WebServiceExposureMode.Public));

        await Fixture.Database.WebServiceReleases().InsertOneAsync(release);
        return release;
    }

    private static ResourceDeploymentAppliedEvent CreateDeploymentEvent(WebServiceRelease release) =>
        new(
            release.Deployments[0].ResourceDeploymentId,
            WebServiceResourceDeployment.ResourceType.Value,
            release.WebServiceId,
            "507f1f77bcf86cd799439052",
            release.Deployments[0].DeploymentGeneration,
            release.Deployments[0].ResourceRevision)
        {
            EventCore = EventCore.Create(release.TenantId)
        };

    private static ResourceDeploymentFailedEvent CreateFailureEvent(
        WebServiceRelease release,
        bool retryable) =>
        new(
            release.Deployments[0].ResourceDeploymentId,
            WebServiceResourceDeployment.ResourceType.Value,
            release.WebServiceId,
            "507f1f77bcf86cd799439052",
            release.Deployments[0].DeploymentGeneration,
            "Apply",
            "ManifestInvalid",
            retryable,
            release.Deployments[0].ResourceRevision)
        {
            EventCore = EventCore.Create(release.TenantId)
        };
}
