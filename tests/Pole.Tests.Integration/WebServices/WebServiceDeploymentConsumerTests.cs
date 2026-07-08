using MongoDB.Driver;
using Pole.Features.Events.Abstractions;
using Pole.Features.Shared.Models;
using Pole.Features.WebServices.Application.Consumers;
using Pole.Features.WebServices.Application.Kubernetes;
using Pole.Features.WebServices.Domain;
using Pole.Features.WebServices.Events;
using Pole.ResultType;
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
        var applier = new FakeDeploymentApplier();
        var consumer = new ApplyWebServiceDeploymentOnWebServiceDeploymentRequestedConsumer(DbContext, applier);

        await consumer.Handle(CreateDeploymentEvent(release));

        var saved = await DbContext.WebServiceReleases
            .Find(r => r.Id == release.Id)
            .SingleAsync();
        Assert.Equal(1, applier.CallCount);
        Assert.Equal(WebServiceReleaseOutcome.Succeeded, saved.Outcome);
        Assert.NotNull(saved.CompletedAtUtc);
        Assert.Null(saved.FailureMessage);
    }

    [Fact]
    public async Task Handle_MarksReleaseFailed_AndRethrows_WhenDeploymentApplyFails()
    {
        var release = await InsertPendingReleaseAsync();
        var applier = new FakeDeploymentApplier("apply failed");
        var consumer = new ApplyWebServiceDeploymentOnWebServiceDeploymentRequestedConsumer(DbContext, applier);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            consumer.Handle(CreateDeploymentEvent(release)));

        var saved = await DbContext.WebServiceReleases
            .Find(r => r.Id == release.Id)
            .SingleAsync();
        Assert.Equal("apply failed", exception.Message);
        Assert.Equal(WebServiceReleaseOutcome.Failed, saved.Outcome);
        Assert.NotNull(saved.CompletedAtUtc);
        Assert.Equal("apply failed", saved.FailureMessage);
    }

    private async Task<WebServiceRelease> InsertPendingReleaseAsync()
    {
        var release = WebServiceReleaseFactory.CreatePending(
            TestData.TenantId,
            "web-service-1",
            2,
            WebServiceReleaseAction.Redeploy,
            TestData.UserId,
            TestData.UserEmail,
            TestData.ValidWebServiceSpecSnapshot(),
            "trace-1",
            "correlation-1");

        await DbContext.WebServiceReleases.InsertOneAsync(release);
        return release;
    }

    private static WebServiceDeploymentRequestedEvent CreateDeploymentEvent(WebServiceRelease release) =>
        new(
            release.Id,
            release.Revision,
            release.Action.ToString(),
            release.WebServiceId,
            "web-api",
            "resource-group-1",
            new DnsRecordShortInfo("dns-1", "api.example.com"),
            release.Spec)
        {
            EventCore = EventCore.Create(release.TenantId)
        };

    private sealed class FakeDeploymentApplier(string? failureMessage = null) : IWebServiceDeploymentApplier
    {
        public int CallCount { get; private set; }

        public Task<Result> ApplyAsync(WebServiceDeploymentRequestedEvent deploymentEvent, CancellationToken cancellationToken = default)
        {
            CallCount++;
            if (failureMessage is not null)
            {
                return Task.FromResult<Result>(Result.ExternalService(
                    failureMessage,
                    "Kubernetes",
                    failureMessage));
            }

            return Task.FromResult<Result>(Result.Success());
        }
    }
}
