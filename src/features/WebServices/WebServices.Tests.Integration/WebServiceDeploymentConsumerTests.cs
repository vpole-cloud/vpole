// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.Events.Abstractions;
using Pole.Features.ResourceDeployments.Events;
using Pole.Features.Shared.Models;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Domain;
using Xunit;

namespace Pole.Features.WebServices.Tests.Integration;

[Collection(nameof(WebServicesMongoCollection))]
public sealed class WebServiceDeploymentConsumerTests(
    WebServicesMongoFixture fixture
) : IAsyncLifetime
{
    private const string CompletionEventType = "pole.web-services/v1/web-service-deployment-completed";

    public Task InitializeAsync() => fixture.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Handle_MarksReleaseSucceeded_WhenDeploymentApplySucceeds()
    {
        var release = await InsertPendingReleaseAsync();
        var consumer = fixture.CreateDeploymentConsumer();

        await consumer.Handle(CreateDeploymentEvent(release));

        var saved = await fixture.Releases.Find(r => r.Id == release.Id).SingleAsync();
        Assert.Equal(WebServiceReleaseOutcome.Succeeded, saved.Outcome);
        Assert.NotNull(saved.CompletedAtUtc);
        Assert.Null(saved.FailureMessage);
    }

    [Fact]
    public async Task Handle_LeavesReleasePending_WhenAppliedGenerationDoesNotMatch()
    {
        var release = await InsertPendingReleaseAsync();
        var consumer = fixture.CreateDeploymentConsumer();
        var applied = CreateDeploymentEvent(release) with
        {
            Generation = release.Deployments[0].DeploymentGeneration + 1,
        };

        await consumer.Handle(applied);

        var saved = await fixture.Releases.Find(r => r.Id == release.Id).SingleAsync();
        Assert.Equal(WebServiceReleaseOutcome.Pending, saved.Outcome);
        Assert.Null(saved.CompletedAtUtc);
        Assert.Equal(
            0L,
            await fixture.OutboxMessages.CountDocumentsAsync(message => message.EventType == CompletionEventType));
    }

    [Fact]
    public async Task Handle_DoesNotPublishAnotherCompletion_WhenDeliveryIsRepeated()
    {
        var release = await InsertPendingReleaseAsync();
        var consumer = fixture.CreateDeploymentConsumer();
        var deployment = CreateDeploymentEvent(release);

        await consumer.Handle(deployment);
        await consumer.Handle(deployment);

        var completionEvents = await fixture.OutboxMessages
            .Find(message => message.EventType == CompletionEventType)
            .ToListAsync();
        Assert.Single(completionEvents);
    }

    [Fact]
    public async Task Handle_MarksReleaseFailed_WhenDeploymentFailureIsTerminal()
    {
        var release = await InsertPendingReleaseAsync();
        var consumer = fixture.CreateDeploymentConsumer();

        await consumer.Handle(CreateFailureEvent(release, retryable: false));

        var saved = await fixture.Releases.Find(item => item.Id == release.Id).SingleAsync();
        Assert.Equal(WebServiceReleaseOutcome.Failed, saved.Outcome);
        Assert.NotNull(saved.CompletedAtUtc);
        Assert.Contains("ManifestInvalid", saved.FailureMessage, StringComparison.Ordinal);
        Assert.Equal(
            1L,
            await fixture.OutboxMessages.CountDocumentsAsync(message => message.EventType == CompletionEventType));
    }

    [Fact]
    public async Task Handle_LeavesReleasePending_WhenDeploymentFailureIsRetryable()
    {
        var release = await InsertPendingReleaseAsync();
        var consumer = fixture.CreateDeploymentConsumer();

        await consumer.Handle(CreateFailureEvent(release, retryable: true));

        var saved = await fixture.Releases.Find(item => item.Id == release.Id).SingleAsync();
        Assert.Equal(WebServiceReleaseOutcome.Pending, saved.Outcome);
        Assert.Null(saved.CompletedAtUtc);
        Assert.Equal(
            0L,
            await fixture.OutboxMessages.CountDocumentsAsync(message => message.EventType == CompletionEventType));
    }

    private async Task<WebServiceRelease> InsertPendingReleaseAsync()
    {
        var probe = new ProbeConfig("/health", "8080", InitialDelaySeconds: 5);
        var spec = new WebServiceSpec(
            new ImageInfo("docker.io", "nginx", "1.27", SecretName: null),
            new WebServiceConfig
            {
                EnvironmentVariables = ImmutableDictionary<string, string>.Empty.Add(
                    "ASPNETCORE_ENVIRONMENT",
                    "Development"),
                PortMappings = [new PortMapping(80, "8080", "TCP", "http")],
            },
            Autoscaling: null,
            new ProbeSpec(probe, probe, probe));
        var release = WebServiceReleaseFactory.CreatePending(
            "507f1f77bcf86cd799439011",
            "507f1f77bcf86cd799439041",
            2,
            WebServiceReleaseAction.Redeploy,
            "test-user",
            "test-user@example.test",
            spec,
            [
                new WebServiceReleaseDeployment(
                    "507f1f77bcf86cd799439051",
                    "507f1f77bcf86cd799439052",
                    ResourceRevision: 2,
                    DeploymentGeneration: 3,
                    WebServiceReleaseDeploymentOperation.Apply,
                    WebServiceReleaseDeploymentOutcome.Pending,
                    CompletedAtUtc: null,
                    FailureCode: null,
                    FailureMessage: null),
            ],
            "trace-1",
            "correlation-1",
            new WebServiceReleaseFactory.CreateOptions(ObjectId.GenerateNewId().ToString(), DateTime.UtcNow),
            WebServiceExposure.Create(WebServiceExposureMode.Public));

        await fixture.Releases.InsertOneAsync(release);
        return release;
    }

    private static ResourceDeploymentAppliedEvent CreateDeploymentEvent(WebServiceRelease release) => new(
        release.Deployments[0].ResourceDeploymentId,
        WebServiceResourceDeployment.ResourceType.Value,
        release.WebServiceId,
        "507f1f77bcf86cd799439052",
        release.Deployments[0].DeploymentGeneration,
        release.Deployments[0].ResourceRevision)
    {
        EventCore = EventCore.Create(release.TenantId),
    };

    private static ResourceDeploymentFailedEvent CreateFailureEvent(WebServiceRelease release, bool retryable) => new(
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
        EventCore = EventCore.Create(release.TenantId),
    };
}
