using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.Events.Abstractions;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.SourceBuilds.Application.Persistence.Jobs;
using Pole.Features.SourceBuilds.Contracts;
using Pole.Features.SourceBuilds.Domain;
using Pole.Features.SourceBuilds.Events;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application.Consumers;
using Pole.Features.WebServices.Application.Endpoints;
using Pole.Features.WebServices.Application.Persistence.Lifecycle;
using Pole.Features.WebServices.Domain;
using Pole.Features.WebServices.Events;
using Pole.ResultType;
using Pole.Tests.Integration.Infrastructure;
using System.Collections.Immutable;
using System.Text.Json;
using Xunit;

namespace Pole.Tests.Integration.SourceBuilds;

public sealed class SourceBuildDeletionTests(MongoReplicaSetFixture fixture)
    : MediatorIntegrationTestBase(fixture)
{
    [Fact]
    public async Task Deletion_RejectsActiveBuilds_ThenCleansOnlyOwnConfigurationAndRetainsHistory()
    {
        var group = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var service = (await Mediator.Send(TestData.CreateWebServiceRequest(group.Id))).AssertSuccess();
        var configuration = Configuration(service.WebServiceId);
        var other = configuration with
        {
            Id = ObjectId.GenerateNewId().ToString(),
            TenantId = ObjectId.GenerateNewId().ToString()
        };
        await DbContext.SourceBuildConfigurations.InsertManyAsync([configuration, other]);
        await DbContext.SourceBuildJobs.InsertOneAsync(Job(other));
        var job = Job(configuration);
        var queue = Services.GetRequiredService<QueueSourceBuildJob>();
        (await queue(job, Requested(job))).AssertSuccess();
        var before = await Fixture.Database.WebServices()
            .Find(x => x.Id == service.WebServiceId)
            .SingleAsync();
        var outboxBefore = await DbContext.OutboxMessages.CountDocumentsAsync(x => true);
        var outcome = (await Mediator.Send(new DeleteWebServiceRequest(
            service.WebServiceId,
            TestData.TenantId))).AssertSuccess<DeleteWebServiceOutcome>();
        var rejection = Assert.IsType<DeleteWebServiceError>(outcome.Value);
        Assert.IsType<SourceBuildWebServiceHasActiveJobs>(rejection.Value);
        var unchanged = await Fixture.Database.WebServices()
            .Find(x => x.Id == service.WebServiceId)
            .SingleAsync();
        Assert.IsType<ActiveWebServiceLifecycle>(unchanged.Lifecycle);
        Assert.Equal(before.Revision, unchanged.Revision);
        Assert.Equal(outboxBefore, await DbContext.OutboxMessages.CountDocumentsAsync(x => true));

        var completed = job.Fail(SourceBuildPhase.Queued, "test", "finished", DateTime.UtcNow);
        await DbContext.SourceBuildJobs.ReplaceOneAsync(x => x.Id == job.Id, completed);
        (await Mediator.Send(new DeleteWebServiceRequest(
            service.WebServiceId,
            TestData.TenantId))).AssertSuccess();

        // Configuration remains until final deletion, but the target already rejects admission.
        var deniedJob = Job(configuration);
        var denied = await queue(deniedJob, Requested(deniedJob));
        Assert.False(denied.TryGetValue(out _, out _));
        Assert.Equal(
            1,
            await DbContext.SourceBuildJobs.CountDocumentsAsync(x => x.TenantId == TestData.TenantId));
        var placement = await DbContext.ResourceDeployments
            .Find(x => x.ResourceId == service.WebServiceId)
            .SingleAsync();
        var now = placement.UpdatedAtUtc.AddSeconds(1);
        var claim = ObjectId.GenerateNewId().ToString();
        var deleted = placement
            .Start(
                placement.DesiredGeneration,
                ResourceDeploymentOperation.Delete,
                claim,
                now,
                now.AddMinutes(1))
            .Complete(
                placement.DesiredGeneration,
                ResourceDeploymentOperation.Delete,
                claim,
                [],
                now.AddSeconds(1));
        await DbContext.ResourceDeployments.ReplaceOneAsync(x => x.Id == placement.Id, deleted);
        (await Services.GetRequiredService<FinalizeWebServiceDeletion>()(
            TestData.TenantId,
            service.WebServiceId)).AssertSuccess();

        var envelope = await DbContext.OutboxMessages
            .Find(x => x.EventType == "pole.web-services/v1/web-service-deleted")
            .SingleAsync();
        var message = JsonSerializer.Deserialize<WebServiceDeletedEvent>(
            envelope.PayloadJson,
            EventExtensions.SerializerOptions)!;
        Assert.Equal(TestData.TenantId, message.EventCore.TenantId);
        var consumer = new CleanupSourceBuildConfigurationConsumer(
            Services.GetRequiredService<CleanupSourceBuildConfigurations>());
        await consumer.HandleAsync(message);
        await consumer.HandleAsync(message);

        Assert.Equal(
            other.Id,
            (await DbContext.SourceBuildConfigurations
                .Find(FilterDefinition<SourceBuildConfiguration>.Empty)
                .SingleAsync()).Id);
        Assert.Equal(
            job.Id,
            (await Services.GetRequiredService<LoadSourceBuildJobPage>()(
                new(
                    TestData.TenantId,
                    configuration.Id,
                    service.WebServiceId,
                    1,
                    20))).Items.Single().Id);

        var missingJob = Job(configuration);
        Assert.False((await queue(missingJob, Requested(missingJob))).TryGetValue(out _, out _));
    }

    [Fact]
    public async Task Admission_InvalidatesStaleDeletionSnapshotWithoutChangingDesiredRevision()
    {
        var group = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var created = (await Mediator.Send(TestData.CreateWebServiceRequest(group.Id))).AssertSuccess();
        var snapshot = await Fixture.Database.WebServices()
            .Find(x => x.Id == created.WebServiceId)
            .SingleAsync();
        var configuration = Configuration(created.WebServiceId);
        await DbContext.SourceBuildConfigurations.InsertOneAsync(configuration);
        var job = Job(configuration);
        (await Services.GetRequiredService<QueueSourceBuildJob>()(job, Requested(job))).AssertSuccess();
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        var staleDeletion = await Services.GetRequiredService<BeginWebServiceDeletionOutcome>()(
            session,
            snapshot,
            snapshot.BeginDeletion(DateTime.UtcNow));
        Assert.False(staleDeletion.TryGetValue(out _, out _));
        await session.AbortTransactionAsync();
        var stored = await Fixture.Database.WebServices()
            .Find(x => x.Id == created.WebServiceId)
            .SingleAsync();
        Assert.IsType<ActiveWebServiceLifecycle>(stored.Lifecycle);
        Assert.Equal(snapshot.ActiveRevision, stored.ActiveRevision);
        Assert.Equal(snapshot.Revision + 1, stored.Revision);
    }

    [Fact]
    public async Task ConcurrentQueueAndDeletion_NeverCommitBoth()
    {
        var group = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var service = (await Mediator.Send(TestData.CreateWebServiceRequest(group.Id))).AssertSuccess();
        var configuration = Configuration(service.WebServiceId);
        await DbContext.SourceBuildConfigurations.InsertOneAsync(configuration);
        var job = Job(configuration);
        var queue = Services.GetRequiredService<QueueSourceBuildJob>()(job, Requested(job));
        var deletion = Mediator.Send(new DeleteWebServiceRequest(
            service.WebServiceId,
            TestData.TenantId));
        await Task.WhenAll(queue, deletion);
        var stored = await Fixture.Database.WebServices()
            .Find(x => x.Id == service.WebServiceId)
            .SingleAsync();
        var jobs = await DbContext.SourceBuildJobs.CountDocumentsAsync(x => x.WebServiceId == service.WebServiceId);
        Assert.False(stored.Lifecycle is DeletingWebServiceLifecycle && jobs != 0);
        Assert.True(jobs == 1 || stored.Lifecycle is DeletingWebServiceLifecycle);
    }

    private static SourceBuildConfiguration Configuration(string serviceId) => new(
        ObjectId.GenerateNewId().ToString(),
        TestData.TenantId,
        ObjectId.GenerateNewId().ToString(),
        serviceId,
        new(
            "Dockerfile:.",
            SourceBuildRecipeKind.Dockerfile,
            ".",
            "Dockerfile",
            ImmutableList<string>.Empty,
            null,
            "FROM scratch",
            false),
        TestData.UserId,
        DateTime.UtcNow,
        DateTime.UtcNow);

    private static SourceBuildJob Job(SourceBuildConfiguration configuration) =>
        SourceBuildJobFactory.CreateManual(
            configuration.TenantId,
            configuration.Id,
            configuration.SourceRepositoryId,
            configuration.WebServiceId,
            "main",
            "abc123",
            configuration.Recipe,
            TestData.UserId,
            null,
            new(ObjectId.GenerateNewId().ToString(), DateTime.UtcNow));

    private static SourceBuildRequestedEvent Requested(SourceBuildJob job) => new(job.Id)
    {
        EventCore = EventCore.Create(job.TenantId)
    };
}
