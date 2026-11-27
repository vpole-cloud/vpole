// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.Events.Abstractions;
using Pole.Features.GitProviders.Application;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.SourceBuilds.Application.Common;
using Pole.Features.SourceBuilds.Application.Configurations;
using Pole.Features.SourceBuilds.Application.Execution;
using Pole.Features.SourceBuilds.Application.Execution.Queueing;
using Pole.Features.SourceBuilds.Contracts;
using Pole.Features.SourceBuilds.Domain;
using Pole.Features.SourceBuilds.Events;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application.Configuration;
using Pole.Features.WebServices.Application.Consumers;
using Pole.Features.WebServices.Application.Endpoints;
using Pole.Features.WebServices.Application.Lifecycle;
using Pole.Features.WebServices.Domain;
using Pole.Features.WebServices.Events;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.SourceBuilds;

[Collection(IntegrationTestCollection.Tenant2)]
public sealed class SourceBuildDeletionTests(
    MongoReplicaSetFixture fixture
) : MediatorIntegrationTestBase(fixture)
{
    protected override void ConfigureServices(IServiceCollection services) =>
        services.AddGitProvidersFeature(new ConfigurationBuilder().Build());

    [Fact]
    public async Task Deletion_RejectsActiveBuilds_ThenCleansOnlyOwnConfigurationAndRetainsHistory()
    {
        var group = await DbContext.InsertResourceGroupAsync(Data.SeedResourceGroup());
        var service = (await Mediator.Send(SourceBuildTestData.CreateTargetRequest(Data, group.Id))).AssertSuccess();
        var configuration = Configuration(service.WebServiceId);
        await PersistConfigurationAsync(configuration);
        var other = configuration with
        {
            Id = ObjectId.GenerateNewId().ToString(),
            TenantId = ObjectId.GenerateNewId().ToString(),
        };
        await DbContext.SourceBuildConfigurations.InsertOneAsync(other);
        await DbContext.SourceBuildJobs.InsertOneAsync(Job(other));
        var job = Job(configuration);
        var queue = Services.GetRequiredService<QueueSourceBuildJob>();
        Assert.IsType<SourceBuildJob>((await queue(job, Requested(job))).Value);
        var before = await Fixture.Database.WebServices().Find(x => x.Id == service.WebServiceId).SingleAsync();
        var outboxBefore = await DbContext.OutboxMessages.CountDocumentsAsync(item => item.TenantId == Data.TenantId);
        var outcome = (await Mediator.Send(new DeleteWebServiceRequest(service.WebServiceId, Data.TenantId)))
            .AssertSuccess<DeleteWebServiceOutcome>();
        var rejection = Assert.IsType<DeleteWebServiceError>(outcome.Value);
        Assert.IsType<SourceBuildWebServiceHasActiveJobs>(rejection.Value);
        var unchanged = await Fixture.Database.WebServices().Find(x => x.Id == service.WebServiceId).SingleAsync();
        Assert.IsType<AwaitingFirstArtifactWebServiceLifecycle>(unchanged.Lifecycle);
        Assert.Equal(before.Revision, unchanged.Revision);
        Assert.Equal(
            outboxBefore,
            await DbContext.OutboxMessages.CountDocumentsAsync(item => item.TenantId == Data.TenantId));

        var completed = job.Fail(SourceBuildPhase.Queued, "test", "finished", DateTime.UtcNow);
        await DbContext.SourceBuildJobs.ReplaceOneAsync(x => x.Id == job.Id, completed);
        (await Mediator.Send(new DeleteWebServiceRequest(service.WebServiceId, Data.TenantId))).AssertSuccess();

        // Configuration remains until final deletion, but the target already rejects admission.
        var deniedJob = Job(configuration) with { InputVersion = new SourceBuildInputVersion(1, 1, 2) };
        var denied = await queue(deniedJob, Requested(deniedJob));
        Assert.IsType<SourceBuildWebServiceNotFound>(denied.Value);
        Assert.Equal(1, await DbContext.SourceBuildJobs.CountDocumentsAsync(x => x.TenantId == Data.TenantId));
        var placement = await DbContext.ResourceDeployments
            .Find(x => x.ResourceId == service.WebServiceId)
            .SingleAsync();
        var now = placement.UpdatedAtUtc.AddSeconds(1);
        var claim = ObjectId.GenerateNewId().ToString();
        var deleted = placement
            .Start(placement.DesiredGeneration, ResourceDeploymentOperation.Delete, claim, now, now.AddMinutes(1))
            .Complete(placement.DesiredGeneration, ResourceDeploymentOperation.Delete, claim, [], now.AddSeconds(1));
        await DbContext.ResourceDeployments.ReplaceOneAsync(x => x.Id == placement.Id, deleted);
        (await Services.GetRequiredService<FinalizeWebServiceDeletion>()(Data.TenantId, service.WebServiceId))
            .AssertSuccess();

        var envelope = await DbContext.OutboxMessages
            .Find(x => x.TenantId == Data.TenantId && x.EventType == "pole.web-services/v1/web-service-deleted")
            .SingleAsync();
        var message = JsonSerializer.Deserialize<WebServiceDeletedEvent>(
            envelope.PayloadJson,
            EventExtensions.SerializerOptions)!;
        Assert.Equal(Data.TenantId, message.EventCore.TenantId);
        var consumer = new CleanupSourceBuildConfigurationConsumer(
            Services.GetRequiredService<CleanupSourceBuildConfigurations>());
        await consumer.HandleAsync(message);
        await consumer.HandleAsync(message);

        Assert.False(
            await DbContext.SourceBuildConfigurations
                .Find(item => item.TenantId == configuration.TenantId && item.Id == configuration.Id)
                .AnyAsync());
        Assert.Equal(
            other.Id,
            (await DbContext.SourceBuildConfigurations
                .Find(item => item.TenantId == other.TenantId && item.Id == other.Id)
                .SingleAsync()).Id);
        Assert.Equal(
            job.Id,
            (await Services.GetRequiredService<LoadSourceBuildJobPage>()(
                new SourceBuildJobListQuery(Data.TenantId, configuration.Id, service.WebServiceId, 1, 20))).Items
            .Single()
            .Id);

        var missingJob = Job(configuration);
        Assert.IsType<SourceBuildConfigurationUpdateConflict>((await queue(missingJob, Requested(missingJob))).Value);
    }

    [Fact]
    public async Task Admission_InvalidatesStaleDeletionSnapshotWithoutChangingDesiredRevision()
    {
        var group = await DbContext.InsertResourceGroupAsync(Data.SeedResourceGroup());
        var created = (await Mediator.Send(SourceBuildTestData.CreateTargetRequest(Data, group.Id))).AssertSuccess();
        var configuration = Configuration(created.WebServiceId);
        await PersistConfigurationAsync(configuration);
        var snapshot = await Fixture.Database.WebServices().Find(x => x.Id == configuration.WebServiceId).SingleAsync();
        var job = Job(configuration);
        Assert.IsType<SourceBuildJob>(
            (await Services.GetRequiredService<QueueSourceBuildJob>()(job, Requested(job))).Value);
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        var staleDeletion = await Services.GetRequiredService<BeginWebServiceDeletionOutcome>()(
            session,
            snapshot,
            snapshot.BeginDeletion(DateTime.UtcNow));
        Assert.IsType<WebServiceUpdateConflict>(staleDeletion.Value);
        await session.AbortTransactionAsync();
        var stored = await Fixture.Database.WebServices().Find(x => x.Id == created.WebServiceId).SingleAsync();
        Assert.IsType<AwaitingFirstArtifactWebServiceLifecycle>(stored.Lifecycle);
        Assert.Equal(snapshot.ActiveRevision, stored.ActiveRevision);
        Assert.Equal(snapshot.Revision, stored.Revision);
        Assert.Equal(snapshot.PlanningFence + 1, stored.PlanningFence);
    }

    [Fact]
    public async Task ConcurrentQueueAndDeletion_NeverCommitBoth()
    {
        var group = await DbContext.InsertResourceGroupAsync(Data.SeedResourceGroup());
        var service = (await Mediator.Send(SourceBuildTestData.CreateTargetRequest(Data, group.Id))).AssertSuccess();
        var configuration = Configuration(service.WebServiceId);
        await PersistConfigurationAsync(configuration);
        var job = Job(configuration);
        var queue = Services.GetRequiredService<QueueSourceBuildJob>()(job, Requested(job));
        var deletion = Mediator.Send(new DeleteWebServiceRequest(service.WebServiceId, Data.TenantId));
        await Task.WhenAll(queue, deletion);
        var stored = await Fixture.Database.WebServices().Find(x => x.Id == service.WebServiceId).SingleAsync();
        var jobs = await DbContext.SourceBuildJobs.CountDocumentsAsync(x => x.WebServiceId == service.WebServiceId);
        Assert.False(stored.Lifecycle is DeletingWebServiceLifecycle && jobs != 0);
        Assert.True(jobs == 1 || stored.Lifecycle is DeletingWebServiceLifecycle);
    }

    private async Task PersistConfigurationAsync(SourceBuildConfiguration configuration)
    {
        await SourceBuildTestData.SeedRepositoryAsync(DbContext, configuration, Data.UserId);
        Assert.IsType<SourceBuildConfiguration>(
            (await Services.GetRequiredService<CreateSourceBuildConfiguration>()(
                new CreateSourceBuildConfigurationCommand(configuration, 1))).Value);
    }

    private SourceBuildConfiguration Configuration(string serviceId) => SourceBuildConfigurationFactory.Create(
        Data.TenantId,
        "application-build",
        ObjectId.GenerateNewId().ToString(),
        serviceId,
        new SourceBuildRecipe("FROM scratch"),
        new ManualSourceBuildConfigurationOrigin(Data.UserId),
        SourceBuildConfigurationFactory.DefaultOptions);

    private SourceBuildJob Job(SourceBuildConfiguration configuration) => SourceBuildJobFactory.CreateManual(
        configuration.TenantId,
        configuration.Id,
        configuration.SourceRepositoryId,
        configuration.WebServiceId,
        "main",
        "abc123",
        new SourceBuildInputVersion(1, 1, 1),
        configuration.Recipe,
        Data.UserId,
        null,
        new SourceBuildJobFactory.CreateOptions(ObjectId.GenerateNewId().ToString(), DateTime.UtcNow));

    private static SourceBuildRequestedEvent Requested(SourceBuildJob job) => new(job.Id)
    {
        EventCore = EventCore.Create(job.TenantId),
    };
}
