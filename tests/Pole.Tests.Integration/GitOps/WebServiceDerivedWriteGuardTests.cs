// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Pole.Features.Events.Abstractions;
using Pole.Features.SourceBuilds.Contracts;
using Pole.Features.WebServices.Application.Configuration;
using Pole.Features.WebServices.Domain;
using Pole.Features.WebServices.Events;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.GitOps;

[Collection(IntegrationTestCollection.Tenant1)]
public sealed class WebServiceDerivedWriteGuardTests(
    MongoReplicaSetFixture fixture
) : DeclarativePipelineTestBase(fixture)
{
    [Theory]
    [InlineData("name")]
    [InlineData("group")]
    [InlineData("scope")]
    [InlineData("fence")]
    public async Task ArtifactCapabilityRejectsChangesOutsideItsDerivedConfiguration(string field)
    {
        await SeedApplicationConnectionAsync();
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, (await Apply(SourceDeliverySnapshot())).Status);
        var current = await WebServices.Find(item => item.TenantId == Data.TenantId && item.Name == "demo-api")
            .SingleAsync();
        var jobId = Id();
        var configurationId = current.Spec.Image.SourceBuildConfigurationId!;
        var artifact = DeliveredSourceBuildImage.Create(
            configurationId,
            new ImageInfo("registry.test", "app", "1", "pull"),
            "sha256:" + new string('d', 64),
            jobId,
            1);
        var next = current.PromoteArtifact(artifact);
        next = field switch
        {
            "name" => next with { Name = "another-name" },
            "group" => next with { ResourceGroupId = Id() },
            "scope" => next with { IsolationScopeId = Id() },
            "fence" => next with { PlanningFence = next.PlanningFence + 1 },
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };
        var events = await BusinessEvents();
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        var promotion = new SourceBuildPromotionRequest(
            current.TenantId,
            jobId,
            current.Id,
            new SourceBuildDeploymentAuthority(configurationId, 1, 1, "worker", 1),
            new SourceBuildPromotionInput(
                Id(),
                new string('a', 40),
                new SourceBuildPromotionArtifact("registry.test/app:1", artifact.Digest)));
        var snapshot = TestData.ValidWebServiceSpecSnapshot() with
        {
            Image = new DeliveredSourceBuildImageSnapshot(
                configurationId,
                new WebServiceImageInfoSnapshot("registry.test", "app", "1", "pull"),
                artifact.Digest,
                jobId,
                1),
        };
        var changed = new WebServiceUpdatedEvent(
            next.Id,
            next.Name,
            next.ResourceGroupId,
            next.IsolationScopeId,
            next.ActiveRevision,
            WebServiceLifecycleState.Active,
            snapshot,
            new WebServiceExposureSnapshot(next.Exposure.Mode.ToString(), next.Exposure.GeneratedHostnameLabelPrefix),
            next.Tags,
            []) { EventCore = EventCore.Create(next.TenantId) };
        var outcome = await Services.GetRequiredService<PersistWebServiceArtifactPromotion>()(
            new WebServiceArtifactPromotionWrite(current, next, changed, promotion),
            session,
            default);
        Assert.Equal(
            WebServiceUpdateConflictReason.GitOwnedConfiguration,
            Assert.IsType<WebServiceUpdateConflict>(outcome.Value).Reason);
        await session.AbortTransactionAsync();
        Assert.Equal(current.Revision, (await WebServices.Find(item => item.Id == current.Id).SingleAsync()).Revision);
        Assert.Equal(events, await BusinessEvents());
        Assert.Equal(0, await Releases());
    }
}
